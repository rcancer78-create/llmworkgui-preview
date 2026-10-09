using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Application.Watchdogs;

public sealed class ExecutionWatchdog : IExecutionWatchdog
{
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(45);

    private readonly IProcessSupervisor _processSupervisor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExecutionWatchdog> _logger;

    public ExecutionWatchdog(
        IProcessSupervisor processSupervisor,
        TimeProvider? timeProvider = null,
        ILogger<ExecutionWatchdog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);

        _processSupervisor = processSupervisor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ExecutionWatchdog>.Instance;
    }

    public async Task<ExecutionTurnResult> WatchAsync(
        ExecutionWatchdogRequest request,
        IProgress<ExecutionWatchdogObservation>? observationProgress = null,
        CancellationToken turnCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Specification.ExecutionId);
        ValidateRequest(request);

        var startedAt = _timeProvider.GetUtcNow();
        var observer = new TurnObserver(
            request.Specification.ExecutionId,
            observationProgress,
            startedAt,
            _logger);

        if (turnCancellationToken.IsCancellationRequested)
        {
            return CreateResult(
                request,
                ExecutionTurnOutcome.UserCancelled,
                observer,
                executionResult: null,
                startedAt,
                "The turn was cancelled before the process was started.");
        }

        using var turnTimeoutCts = new CancellationTokenSource(request.TurnHardTimeout, _timeProvider);

        // A turn timeout or turn cancellation never terminates a long-lived backend process;
        // only the caller-owned process lifetime token does. The token is passed through
        // directly so the process lifetime stays bound to the caller even after this turn
        // completed and the watchdog returned.
        using var shortLivedProcessCts = request.ProcessIsLongLived
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                turnCancellationToken,
                request.ProcessLifetimeToken,
                turnTimeoutCts.Token);

        var processToken = request.ProcessIsLongLived
            ? request.ProcessLifetimeToken
            : shortLivedProcessCts!.Token;

        using var observationCts = new CancellationTokenSource();
        using var userCancellationWaitCts = CancellationTokenSource.CreateLinkedTokenSource(turnCancellationToken);

        try
        {
            // Construct all potentially failing deadline infrastructure before launching a process.
            var userCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, userCancellationWaitCts.Token);
            var turnTimeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, turnTimeoutCts.Token);
            var sessionTimeoutTask = CreateSessionTimeoutTask(request, observationCts.Token);
            if (turnCancellationToken.IsCancellationRequested)
            {
                return CreateResult(request, ExecutionTurnOutcome.UserCancelled, observer,
                    executionResult: null, startedAt, "The turn was cancelled before the process was started.");
            }

            Task<ProcessExecutionResult> executionTask;
            observer.ProcessDispatched = true;
            try
            {
                executionTask = _processSupervisor.ExecuteAsync(request.Specification, observer, processToken);
            }
            catch (Exception exception)
            {
                // A synchronous implementation failure has the same outcome as a faulted task.
                executionTask = Task.FromException<ProcessExecutionResult>(exception);
            }
            _ = ObserveSilenceAsync(observer, executionTask, request.SilenceObservationInterval, observationCts.Token);

            while (true)
            {
                var completed = await Task.WhenAny(
                        executionTask,
                        userCancellationTask,
                        turnTimeoutTask,
                        sessionTimeoutTask)
                    .ConfigureAwait(false);

                if (executionTask.IsCompleted)
                {
                    return await ClassifyExecutionAsync(
                            request,
                            executionTask,
                            observer,
                            startedAt,
                            turnTimeoutCts)
                        .ConfigureAwait(false);
                }

                if (completed == userCancellationTask)
                {
                    observer.Publish(
                        ExecutionWatchdogObservationKind.UserCancellationRequested,
                        _timeProvider.GetUtcNow(),
                        "Turn cancellation was requested.");

                    var cancelledExecutionResult = await StopProcessAsync(request, shortLivedProcessCts, executionTask)
                        .ConfigureAwait(false);

                    return CreateResult(
                        request,
                        ExecutionTurnOutcome.UserCancelled,
                        observer,
                        cancelledExecutionResult,
                        startedAt,
                        "The turn was cancelled by the caller.");
                }

                if (completed == turnTimeoutTask)
                {
                    observer.Publish(
                        ExecutionWatchdogObservationKind.TurnHardTimeoutElapsed,
                        _timeProvider.GetUtcNow(),
                        $"Turn hard timeout of {request.TurnHardTimeout} elapsed.");

                    var timedOutExecutionResult = await StopProcessAsync(request, shortLivedProcessCts, executionTask)
                        .ConfigureAwait(false);

                    return CreateResult(
                        request,
                        ExecutionTurnOutcome.TimedOut,
                        observer,
                        timedOutExecutionResult,
                        startedAt,
                        $"The turn did not complete within the hard timeout of {request.TurnHardTimeout}.");
                }

                if (observer.SessionConfirmed)
                {
                    sessionTimeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, observationCts.Token);
                    continue;
                }

                observer.Publish(
                    ExecutionWatchdogObservationKind.SessionConfirmationTimeoutElapsed,
                    _timeProvider.GetUtcNow(),
                    $"Session confirmation timeout of {request.SessionConfirmationTimeout} elapsed.");

                var sessionFailureExecutionResult = await StopProcessAsync(request, shortLivedProcessCts, executionTask)
                    .ConfigureAwait(false);

                return CreateResult(
                    request,
                    ExecutionTurnOutcome.OrchestrationFailure,
                    observer,
                    sessionFailureExecutionResult,
                    startedAt,
                    $"The process stayed alive without session/protocol evidence for {request.SessionConfirmationTimeout}.");
            }
        }
        finally
        {
            observer.Complete();
            observationCts.Cancel();
            // Retire the wait without cancelling the caller's token or its process lifetime.
            userCancellationWaitCts.Cancel();
        }
    }

    private Task CreateSessionTimeoutTask(ExecutionWatchdogRequest request, CancellationToken cancellationToken)
    {
        return request.SessionConfirmationTimeout is { } sessionConfirmationTimeout
            ? Task.Delay(sessionConfirmationTimeout, _timeProvider, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private async Task<ExecutionTurnResult> ClassifyExecutionAsync(
        ExecutionWatchdogRequest request,
        Task<ProcessExecutionResult> executionTask,
        TurnObserver observer,
        DateTimeOffset startedAt,
        CancellationTokenSource turnTimeoutCts)
    {
        ProcessExecutionResult executionResult;

        try
        {
            executionResult = await executionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelledOutcome = ResolveCancelledOutcome(request, turnTimeoutCts);

            return CreateResult(
                request,
                cancelledOutcome,
                observer,
                executionResult: null,
                startedAt,
                "The process execution ended before it reported a result.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "The process execution failed for {ExecutionId}.",
                request.Specification.ExecutionId);

            return CreateResult(
                request,
                ExecutionTurnOutcome.OrchestrationFailure,
                observer,
                executionResult: null,
                startedAt,
                exception.Message);
        }

        if (executionResult.ExitCode is not null && executionResult.TerminationReason is not (ProcessTerminationReason.StartupPending or ProcessTerminationReason.CleanupPending))
            observer.Publish(ExecutionWatchdogObservationKind.ProcessExited, _timeProvider.GetUtcNow(), "The supervised process exited.");

        var outcome = ResolveOutcome(request, executionResult);
        // The short-lived process receives a token linked to the hard deadline, so its
        // completed result may say UserCancelled even when the deadline caused cancellation.
        // ResolveOutcome has already given physical/lifetime uncertainty priority.
        if (outcome == ExecutionTurnOutcome.UserCancelled && turnTimeoutCts.IsCancellationRequested)
            outcome = ExecutionTurnOutcome.TimedOut;

        return CreateResult(
            request,
            outcome,
            observer,
            executionResult,
            startedAt,
            ResolveFailureMessage(outcome, executionResult));
    }

    private static ExecutionTurnOutcome ResolveCancelledOutcome(
        ExecutionWatchdogRequest request,
        CancellationTokenSource turnTimeoutCts)
    {
        if (request.ProcessLifetimeToken.IsCancellationRequested)
        {
            return ExecutionTurnOutcome.Ambiguous;
        }

        return turnTimeoutCts.IsCancellationRequested
            ? ExecutionTurnOutcome.TimedOut
            : ExecutionTurnOutcome.UserCancelled;
    }

    private static ExecutionTurnOutcome ResolveOutcome(
        ExecutionWatchdogRequest request,
        ProcessExecutionResult result)
    {
        if (request.ProcessLifetimeToken.IsCancellationRequested)
        {
            return ExecutionTurnOutcome.Ambiguous;
        }

        switch (result.TerminationReason)
        {
            case ProcessTerminationReason.None:
                break;
            case ProcessTerminationReason.BufferOverflow:
                return ExecutionTurnOutcome.BufferOverflow;
            case ProcessTerminationReason.StartupTimeout:
                return ExecutionTurnOutcome.StartupTimeout;
            case ProcessTerminationReason.StartupPending:
            case ProcessTerminationReason.CleanupPending:
                return ExecutionTurnOutcome.Ambiguous;
            case ProcessTerminationReason.UserCancelled:
                return ExecutionTurnOutcome.UserCancelled;
            case ProcessTerminationReason.TurnTimeout:
            case ProcessTerminationReason.InactivityTimeout:
                return ExecutionTurnOutcome.TimedOut;
            case ProcessTerminationReason.UnexpectedExit:
                return ExecutionTurnOutcome.Failed;
        }

        if (result.ExitCode is null)
        {
            return ExecutionTurnOutcome.Ambiguous;
        }

        if (result.ExitCode == 0)
        {
            return ExecutionTurnOutcome.Succeeded;
        }

        if (ProcessFailureEvidence.IndicatesOrchestrationFailure(result))
        {
            return ExecutionTurnOutcome.OrchestrationFailure;
        }

        if (ProcessFailureEvidence.IndicatesInteractiveInputWait(result))
        {
            return ExecutionTurnOutcome.InteractiveInputWait;
        }

        return ExecutionTurnOutcome.Failed;
    }

    private static string? ResolveFailureMessage(
        ExecutionTurnOutcome outcome,
        ProcessExecutionResult result)
    {
        return outcome switch
        {
            ExecutionTurnOutcome.Succeeded => null,
            ExecutionTurnOutcome.Failed =>
                result.FailureMessage ?? $"The process exited with code {result.ExitCode}.",
            ExecutionTurnOutcome.InteractiveInputWait =>
                "The process requested interactive input while stdin was closed and received EOF.",
            ExecutionTurnOutcome.OrchestrationFailure =>
                result.FailureMessage ?? "The process failed with an orchestration error.",
            ExecutionTurnOutcome.Ambiguous =>
                "The process ended without terminal evidence.",
            ExecutionTurnOutcome.BufferOverflow when result.OutputCaptureIncomplete && !result.OutputLimitExceeded =>
                "Process output capture is incomplete after a local spool/read failure.",
            ExecutionTurnOutcome.BufferOverflow =>
                result.FailureMessage ?? "The bounded output buffer overflowed during the turn.",
            _ => result.FailureMessage
        };
    }

    private async Task<ProcessExecutionResult?> StopProcessAsync(
        ExecutionWatchdogRequest request,
        CancellationTokenSource? processCancellation,
        Task<ProcessExecutionResult> executionTask)
    {
        if (request.ProcessIsLongLived)
        {
            ObserveDetachedExecution(executionTask);
            return null;
        }

        if (processCancellation is not null && !processCancellation.IsCancellationRequested)
        {
            processCancellation.Cancel();
        }

        return await AwaitTerminationAsync(executionTask).ConfigureAwait(false);
    }

    private async Task<ProcessExecutionResult?> AwaitTerminationAsync(
        Task<ProcessExecutionResult> executionTask)
    {
        using var graceCts = new CancellationTokenSource();
        Task completed;
        try
        {
            completed = await Task.WhenAny(executionTask,
                    Task.Delay(TerminationGracePeriod, _timeProvider, graceCts.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            graceCts.Cancel();
        }

        if (completed != executionTask)
        {
            ObserveDetachedExecution(executionTask);

            _logger.LogWarning(
                "The supervised process did not terminate within {GracePeriod}; the turn result will not include its exit evidence.",
                TerminationGracePeriod);

            return null;
        }

        try
        {
            return await executionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to observe the terminated process execution.");
            return null;
        }
    }

    private static void ObserveDetachedExecution(Task<ProcessExecutionResult> executionTask)
    {
        _ = executionTask.ContinueWith(
            static task => { _ = task.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveSilenceAsync(
        TurnObserver observer,
        Task executionTask,
        TimeSpan interval,
        CancellationToken stopToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, _timeProvider, stopToken).ConfigureAwait(false);

                var now = _timeProvider.GetUtcNow();

                if (executionTask.IsCompleted || !observer.TryBeginSilencePeriod(now))
                {
                    continue;
                }

                observer.Publish(
                    ExecutionWatchdogObservationKind.ProcessLiveness,
                    now,
                    "The supervised process is still running.");

                observer.Publish(
                    ExecutionWatchdogObservationKind.ModelSilence,
                    now,
                    "No transport output was observed; model silence is an observation, not a failure.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The execution watchdog silence observer failed.");
        }
    }

    private ExecutionTurnResult CreateResult(
        ExecutionWatchdogRequest request,
        ExecutionTurnOutcome outcome,
        TurnObserver observer,
        ProcessExecutionResult? executionResult,
        DateTimeOffset startedAt,
        string? failureMessage)
    {
        if (executionResult?.TerminationReason is ProcessTerminationReason.StartupPending or ProcessTerminationReason.CleanupPending
            || (executionResult is null && observer.ProcessDispatched))
        {
            outcome = ExecutionTurnOutcome.Ambiguous;
            failureMessage = "Native termination is not confirmed; the outcome is ambiguous and automatic retry is forbidden.";
        }
        return new ExecutionTurnResult
        {
            ExecutionId = request.Specification.ExecutionId,
            Outcome = outcome,
            ConsumesModelRetryBudget = ConsumesModelRetryBudget(outcome)
                && !(outcome == ExecutionTurnOutcome.BufferOverflow
                    && executionResult is { OutputCaptureIncomplete: true, OutputLimitExceeded: false }),
            NormalizedHealthErrorClass = ClassifyHealthFailure(outcome, executionResult),
            ProcessId = executionResult?.ProcessId,
            ExitCode = executionResult?.ExitCode,
            StartedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            SessionConfirmed = observer.SessionConfirmed,
            TransportActivityObserved = observer.TransportActivityObserved,
            ModelSilenceObserved = observer.ModelSilenceObserved,
            FailureMessage = failureMessage
        };
    }

    private static bool ConsumesModelRetryBudget(ExecutionTurnOutcome outcome)
    {
        return outcome switch
        {
            ExecutionTurnOutcome.Failed => true,
            ExecutionTurnOutcome.TimedOut => true,
            ExecutionTurnOutcome.BufferOverflow => true,
            _ => false
        };
    }

    private static HealthErrorClass? ClassifyHealthFailure(
        ExecutionTurnOutcome outcome,
        ProcessExecutionResult? executionResult) => outcome switch
    {
        ExecutionTurnOutcome.Succeeded => null,
        ExecutionTurnOutcome.UserCancelled => HealthErrorClass.UserCancellation,
        ExecutionTurnOutcome.Ambiguous => HealthErrorClass.UnknownOrAmbiguousCompletion,
        ExecutionTurnOutcome.InteractiveInputWait => HealthErrorClass.UnexpectedInteractiveInputWait,
        ExecutionTurnOutcome.OrchestrationFailure when executionResult is not null
            && ProcessFailureEvidence.IndicatesOrchestrationFailure(executionResult) =>
            HealthErrorClass.ShellCompositionOrQuoting,
        ExecutionTurnOutcome.OrchestrationFailure or ExecutionTurnOutcome.StartupTimeout =>
            HealthErrorClass.StartupOrSessionCreation,
        ExecutionTurnOutcome.TimedOut => HealthErrorClass.NetworkOrTimeout,
        _ => HealthErrorClass.UnknownOrAmbiguousCompletion
    };

    private static void ValidateRequest(ExecutionWatchdogRequest request)
    {
        var maximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (request.TurnHardTimeout <= TimeSpan.Zero || request.TurnHardTimeout > maximumTimerDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExecutionWatchdogRequest.TurnHardTimeout),
                request.TurnHardTimeout,
                "TurnHardTimeout must be positive and representable by a timer.");
        }

        if (request.SilenceObservationInterval <= TimeSpan.Zero || request.SilenceObservationInterval > maximumTimerDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExecutionWatchdogRequest.SilenceObservationInterval),
                request.SilenceObservationInterval,
                "SilenceObservationInterval must be positive and representable by a timer.");
        }

        if (request.SessionConfirmationTimeout is { } sessionConfirmationTimeout
            && (sessionConfirmationTimeout <= TimeSpan.Zero || sessionConfirmationTimeout > maximumTimerDelay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExecutionWatchdogRequest.SessionConfirmationTimeout),
                sessionConfirmationTimeout,
                "SessionConfirmationTimeout must be positive and representable by a timer when specified.");
        }
    }

    private sealed class TurnObserver : IProgress<ProcessOutputEvent>
    {
        private readonly string _executionId;
        private readonly IProgress<ExecutionWatchdogObservation>? _progress;
        private readonly ILogger _logger;

        private long _lastActivityTicks;
        private long _lastSilenceTicks;
        private int _sessionConfirmed;
        private int _transportActivityObserved;
        private int _modelSilenceObserved;
        private int _closed;

        public TurnObserver(
            string executionId,
            IProgress<ExecutionWatchdogObservation>? progress,
            DateTimeOffset startedAt,
            ILogger logger)
        {
            _executionId = executionId;
            _progress = progress;
            _logger = logger;
            _lastActivityTicks = startedAt.UtcTicks;
        }

        public bool SessionConfirmed => Volatile.Read(ref _sessionConfirmed) == 1;

        public bool ProcessDispatched { get; set; }

        public bool TransportActivityObserved => Volatile.Read(ref _transportActivityObserved) == 1;

        public bool ModelSilenceObserved => Volatile.Read(ref _modelSilenceObserved) == 1;

        public void Report(ProcessOutputEvent value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (Volatile.Read(ref _closed) == 1)
            {
                return;
            }

            Interlocked.Exchange(ref _transportActivityObserved, 1);
            Interlocked.Exchange(ref _lastActivityTicks, value.TimestampUtc.UtcTicks);

            if (Interlocked.CompareExchange(ref _sessionConfirmed, 1, 0) == 0)
            {
                Publish(ExecutionWatchdogObservationKind.SessionConfirmed, value.TimestampUtc, detail: null);
            }

            Publish(
                ExecutionWatchdogObservationKind.TransportActivity,
                value.TimestampUtc,
                $"{value.StreamKind} {value.BytesCount} bytes");
        }

        public bool TryBeginSilencePeriod(DateTimeOffset now)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                return false;
            }

            var lastActivity = Interlocked.Read(ref _lastActivityTicks);
            var lastSilence = Interlocked.Read(ref _lastSilenceTicks);

            if (lastSilence >= lastActivity)
            {
                return false;
            }

            Interlocked.Exchange(ref _lastSilenceTicks, now.UtcTicks);
            Interlocked.Exchange(ref _modelSilenceObserved, 1);

            return true;
        }

        public void Publish(
            ExecutionWatchdogObservationKind kind,
            DateTimeOffset timestamp,
            string? detail)
        {
            if (Volatile.Read(ref _closed) == 1 || _progress is null)
            {
                return;
            }

            try
            {
                _progress.Report(new ExecutionWatchdogObservation(kind, _executionId, timestamp, detail));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "An execution watchdog observation callback failed.");
            }
        }

        public void Complete()
        {
            Interlocked.Exchange(ref _closed, 1);
        }
    }
}
