using System.Collections.Concurrent;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Watchdogs;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.IntegrationTests.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Watchdogs;

public sealed class ExecutionWatchdogTests
{
    [Fact]
    public async Task WatchAsync_SuccessfulTurn_ClassifiesSucceededWithSessionEvidence()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateOutputSpecification("watchdog-success", "hello", exitCode: 0);
        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        var result = await watchdog.WatchAsync(CreateRequest(specification), progress);

        Assert.Equal(ExecutionTurnOutcome.Succeeded, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ProcessId);
        Assert.True(result.SessionConfirmed);
        Assert.True(result.TransportActivityObserved);
        Assert.False(result.ModelSilenceObserved);
        Assert.Null(result.FailureMessage);
        Assert.True(result.Duration >= TimeSpan.Zero);

        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.SessionConfirmed);
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.TransportActivity);
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.ProcessExited);
    }

    [Fact]
    public async Task WatchAsync_InteractiveInputFailure_ClassifiesInteractiveInputWaitWithoutRetryBudget()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateStdinReadFailureSpecification("watchdog-interactive-input");
        var request = CreateRequest(specification, sessionConfirmationTimeout: TimeSpan.FromSeconds(20));

        var result = await watchdog.WatchAsync(request);

        Assert.Equal(ExecutionTurnOutcome.InteractiveInputWait, result.Outcome);
        Assert.Equal(HealthErrorClass.UnexpectedInteractiveInputWait, result.NormalizedHealthErrorClass);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.Equal(2, result.ExitCode);
        Assert.True(result.TransportActivityObserved);
        Assert.Contains("interactive input", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WatchAsync_MissingRequiredArgument_ClassifiesOrchestrationFailureWithoutRetryBudget()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateRequiredArgumentSpecification("watchdog-missing-argument");

        var result = await watchdog.WatchAsync(CreateRequest(specification));

        Assert.Equal(ExecutionTurnOutcome.OrchestrationFailure, result.Outcome);
        Assert.Equal(HealthErrorClass.ShellCompositionOrQuoting, result.NormalizedHealthErrorClass);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.Equal(3, result.ExitCode);
        Assert.DoesNotContain("interactive input", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WatchAsync_GenericNonZeroExit_ClassifiesFailedAndConsumesRetryBudget()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateGenericFailureSpecification(
            "watchdog-failed",
            "generic backend failure",
            exitCode: 5);

        var result = await watchdog.WatchAsync(CreateRequest(specification));

        Assert.Equal(ExecutionTurnOutcome.Failed, result.Outcome);
        Assert.True(result.ConsumesModelRetryBudget);
        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public async Task WatchAsync_SilentProcessAfterActivity_IsNotFailedBecauseOfSilence()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateSilentPeriodSpecification("watchdog-silence", silenceSeconds: 2);
        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        var request = CreateRequest(
            specification,
            sessionConfirmationTimeout: TimeSpan.FromSeconds(20),
            silenceObservationInterval: TimeSpan.FromMilliseconds(300));

        var result = await watchdog.WatchAsync(request, progress);

        Assert.Equal(ExecutionTurnOutcome.Succeeded, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.True(result.ModelSilenceObserved);
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.ModelSilence);
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.ProcessLiveness);
    }

    [Fact]
    public async Task WatchAsync_SilentProcessCancellation_IsUserCancelledNotFailed()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateHangSpecification("watchdog-silent-cancel");
        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(700));

        var request = CreateRequest(
            specification,
            silenceObservationInterval: TimeSpan.FromMilliseconds(300));

        var result = await watchdog.WatchAsync(request, progress, cancellation.Token);

        Assert.Equal(ExecutionTurnOutcome.UserCancelled, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.True(result.ModelSilenceObserved);
        Assert.NotNull(result.ProcessId);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.UserCancellationRequested);
    }

    [Fact]
    public async Task WatchAsync_TurnHardTimeout_TerminatesPerTurnProcessWithTimedOut()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) }));

        var specification = harness.CreateHangSpecification("watchdog-turn-timeout");
        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        var request = new ExecutionWatchdogRequest
        {
            Specification = specification,
            TurnHardTimeout = TimeSpan.FromSeconds(1),
            SilenceObservationInterval = TimeSpan.FromSeconds(30)
        };

        var result = await watchdog.WatchAsync(request, progress);

        Assert.Equal(ExecutionTurnOutcome.TimedOut, result.Outcome);
        Assert.True(result.ConsumesModelRetryBudget);
        Assert.NotNull(result.ProcessId);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.TurnHardTimeoutElapsed);
    }

    [Fact]
    public async Task WatchAsync_TurnHardTimeout_DoesNotKillLongLivedServerProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) }));

        var pidFilePath = harness.GetPath("long-lived-server.pid");
        var specification = harness.CreateLongLivedServerSpecification(
            "watchdog-long-lived-server",
            pidFilePath);

        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        using var processLifetime = new CancellationTokenSource();

        var request = new ExecutionWatchdogRequest
        {
            Specification = specification,
            ProcessIsLongLived = true,
            ProcessLifetimeToken = processLifetime.Token,
            TurnHardTimeout = TimeSpan.FromSeconds(1),
            SilenceObservationInterval = TimeSpan.FromMilliseconds(300)
        };

        var serverProcessId = 0;

        try
        {
            var result = await watchdog.WatchAsync(request, progress);

            Assert.Equal(ExecutionTurnOutcome.Ambiguous, result.Outcome);
            Assert.False(result.ConsumesModelRetryBudget);
            Assert.True(result.ModelSilenceObserved);

            serverProcessId = await FakeProcessHarness.WaitForChildPidAsync(
                pidFilePath,
                TimeSpan.FromSeconds(20));

            Assert.True(
                FakeProcessHarness.IsProcessAlive(serverProcessId),
                "The turn hard timeout must not terminate a long-lived server process.");

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.True(
                FakeProcessHarness.IsProcessAlive(serverProcessId),
                "The server process must stay alive after the turn hard timeout was reported.");

            Assert.Contains(
                observations,
                observation => observation.Kind == ExecutionWatchdogObservationKind.ModelSilence);
        }
        finally
        {
            processLifetime.Cancel();
        }

        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            serverProcessId,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task WatchAsync_SessionConfirmationTimeout_IsOrchestrationFailureWithoutRetryBudget()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) }));

        var specification = harness.CreateHangSpecification("watchdog-session-timeout");
        var observations = new ConcurrentQueue<ExecutionWatchdogObservation>();
        var progress = new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue);

        var request = new ExecutionWatchdogRequest
        {
            Specification = specification,
            SessionConfirmationTimeout = TimeSpan.FromMilliseconds(500),
            TurnHardTimeout = TimeSpan.FromSeconds(30),
            SilenceObservationInterval = TimeSpan.FromSeconds(30)
        };

        var result = await watchdog.WatchAsync(request, progress);

        Assert.Equal(ExecutionTurnOutcome.OrchestrationFailure, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.False(result.SessionConfirmed);
        Assert.NotNull(result.ProcessId);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
        Assert.Contains(
            observations,
            observation => observation.Kind == ExecutionWatchdogObservationKind.SessionConfirmationTimeoutElapsed);
    }

    [Fact]
    public async Task WatchAsync_ProcessLifetimeCancellation_IsAmbiguous()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) }));

        var pidFilePath = harness.GetPath("ambiguous-server.pid");
        var specification = harness.CreateLongLivedServerSpecification(
            "watchdog-ambiguous-server",
            pidFilePath);

        using var processLifetime = new CancellationTokenSource();
        processLifetime.CancelAfter(TimeSpan.FromMilliseconds(500));

        var request = new ExecutionWatchdogRequest
        {
            Specification = specification,
            ProcessIsLongLived = true,
            ProcessLifetimeToken = processLifetime.Token,
            TurnHardTimeout = TimeSpan.FromSeconds(30),
            SilenceObservationInterval = TimeSpan.FromSeconds(30)
        };

        var result = await watchdog.WatchAsync(request);

        Assert.Equal(ExecutionTurnOutcome.Ambiguous, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
    }

    [Fact]
    public async Task WatchAsync_MissingExecutable_ClassifiesStartupTimeout()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = new ProcessStartSpecification
        {
            ExecutionId = "watchdog-startup-timeout",
            FileName = harness.GetPath("missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            WorkingDirectory = harness.WorkingDirectory
        };

        var result = await watchdog.WatchAsync(CreateRequest(specification));

        Assert.Equal(ExecutionTurnOutcome.StartupTimeout, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.Null(result.ProcessId);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public async Task WatchAsync_BufferOverflow_ClassifiesBufferOverflow()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();

        var supervisor = CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions
            {
                OutputMemoryLimitBytes = 64 * 1024,
                OutputHeadRetentionBytes = 16 * 1024,
                OutputTailRetentionBytes = 16 * 1024,
                GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500)
            });

        var watchdog = CreateWatchdog(supervisor);
        var specification = harness.CreateContinuousOutputSpecification("watchdog-overflow", 20000);

        var result = await watchdog.WatchAsync(CreateRequest(specification));

        Assert.Equal(ExecutionTurnOutcome.BufferOverflow, result.Outcome);
        Assert.True(result.ConsumesModelRetryBudget);
    }

    [Fact]
    public async Task WatchAsync_PreCancelledTurn_IsUserCancelledWithoutStartingProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var watchdog = CreateWatchdog(CreateSupervisor(dataDirectory.Root));

        var specification = harness.CreateOutputSpecification("watchdog-pre-cancelled", "never-runs");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await watchdog.WatchAsync(CreateRequest(specification), turnCancellationToken: cancellation.Token);

        Assert.Equal(ExecutionTurnOutcome.UserCancelled, result.Outcome);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.Null(result.ProcessId);
    }

    private static ExecutionWatchdogRequest CreateRequest(
        ProcessStartSpecification specification,
        TimeSpan? sessionConfirmationTimeout = null,
        TimeSpan? silenceObservationInterval = null)
    {
        return new ExecutionWatchdogRequest
        {
            Specification = specification,
            SessionConfirmationTimeout = sessionConfirmationTimeout,
            TurnHardTimeout = TimeSpan.FromSeconds(30),
            SilenceObservationInterval = silenceObservationInterval ?? TimeSpan.FromSeconds(30)
        };
    }

    private static ProcessSupervisor CreateSupervisor(
        string appDataDirectory,
        ProcessSupervisorOptions? options = null)
    {
        return new ProcessSupervisor(
            Options.Create(options ?? new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = appDataDirectory });
    }

    private static ExecutionWatchdog CreateWatchdog(IProcessSupervisor supervisor)
    {
        return new ExecutionWatchdog(supervisor);
    }
}
