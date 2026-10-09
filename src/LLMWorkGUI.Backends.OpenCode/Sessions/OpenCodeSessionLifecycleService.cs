using System.Text.Json;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.OpenCode.Sessions;

public sealed partial class OpenCodeSessionLifecycleService : IOpenCodeSessionLifecycleService
{
    private const string TextSeparator = "\n\n";

    private readonly IOpenCodeClient _client;
    private readonly IProjectLockRepository? _projectLockRepository;
    private readonly OpenCodeSessionLifecycleOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);

    public OpenCodeSessionLifecycleService(
        IOpenCodeClient client,
        IProjectLockRepository? projectLockRepository = null,
        OpenCodeSessionLifecycleOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _options = options ?? new OpenCodeSessionLifecycleOptions();
        _options.Validate();

        _client = client;
        _projectLockRepository = projectLockRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var response = await _client.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);

        EnsureNativeSessionId(response);

        lock (_gate)
        {
            _sessions[response.Id] = new TrackedSession(
                response,
                request.Model,
                request.Agent,
                Array.Empty<string>());
        }

        return response;
    }

    public async Task<OpenCodeSessionResponse> ContinueSessionAsync(
        string sessionId,
        SessionBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        TrackedSession admitted;
        long generation;
        lock (_gate)
        {
            var tracked = GetRequiredTrackedSession(sessionId);
            EnsureSessionInteractive(tracked);

            if (tracked.ActiveTurn is not null)
            {
                throw new InvalidOperationException(
                    $"Session '{sessionId}' already has an active turn.");
            }

            if (tracked.Binding is not null && !tracked.Binding.Equals(binding))
            {
                throw new InvalidOperationException(
                    $"Session '{sessionId}' must continue with the confirmed immutable binding.");
            }
            if (tracked.ContinuationPending)
                throw new InvalidOperationException("Native continuation is already being confirmed.");
            admitted = tracked;
            generation = ++tracked.OperationGeneration;
            tracked.ContinuationPending = true;
        }

        try
        {
            var refreshed = await _client.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new OpenCodeClientException($"The native session '{sessionId}' no longer exists.");
    
            if (!string.Equals(refreshed.Id, sessionId, StringComparison.Ordinal))
            {
                throw new OpenCodeClientException(
                    $"The native session '{sessionId}' was resolved as '{refreshed.Id}'.");
            }
    
            lock (_gate)
            {
                var tracked = GetRequiredTrackedSession(sessionId);
                EnsureSessionInteractive(tracked);
                if (!ReferenceEquals(tracked, admitted) || tracked.OperationGeneration != generation || tracked.ActiveTurn is not null)
                    throw new InvalidOperationException("The session changed while native continuation was being confirmed.");
                if (tracked.Binding is not null && !tracked.Binding.Equals(binding))
                    throw new InvalidOperationException("The confirmed immutable binding changed during native continuation.");
                tracked.Binding ??= binding;
                tracked.Response = refreshed;
            }
    
            return refreshed;
        }
        finally
        {
            lock (_gate) admitted.ContinuationPending = false;
        }
    }

    public async Task<TurnResult> ExecuteTurnAsync(
        string sessionId,
        OpenCodePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        TurnContext context;

        lock (_gate)
        {
            var tracked = GetRequiredTrackedSession(sessionId);
            EnsureSessionInteractive(tracked);
            if (tracked.ContinuationPending)
                throw new InvalidOperationException("A prompt cannot start before native continuation confirms its binding.");

            if (tracked.ActiveTurn is not null)
            {
                throw new InvalidOperationException(
                    $"Session '{sessionId}' already has an active turn.");
            }

            EnsurePromptMatchesConfirmedBinding(tracked, request);

            if (string.IsNullOrWhiteSpace(request.MessageId) || !request.MessageId.StartsWith("msg", StringComparison.Ordinal)
                || request.MessageId.Length > 128 || request.MessageId.Any(char.IsControl))
                throw new ArgumentException("A valid native prompt message ID is required.", nameof(request));
            if (tracked.PromptMessageIds.Count >= 10000 || !tracked.PromptMessageIds.Add(request.MessageId))
                throw new InvalidOperationException("The prompt message ID must be new; reset the session when its bounded history is full.");

            context = new TurnContext(request.MessageId) { Phase = OpenCodeTurnPhase.Running };
            tracked.OperationGeneration++;
            tracked.ActiveTurn = context;

            if (tracked.State == SessionState.Idle)
            {
                tracked.State = Transition(tracked.State, SessionState.Active);
            }
        }

        var registration = cancellationToken.Register(
            static state => ((TurnContext)state!).RequestCancellation(),
            context);

        try
        {
            return await RunTurnAsync(sessionId, request, context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A transport/programming exception still has to release a concurrent Stop waiter.
            Complete(context, CreateResult(
                sessionId, context.Accumulator, TurnResult.FailedStatus,
                "Execution failed before a native terminal outcome was confirmed."));
            throw;
        }
        finally
        {
            registration.Dispose();

            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var tracked)
                    && ReferenceEquals(tracked.ActiveTurn, context))
                {
                    tracked.ActiveTurn = null;

                    if (tracked.State == SessionState.Active)
                    {
                        tracked.State = Transition(tracked.State,
                            context.Completion.Task.IsCompletedSuccessfully && context.Completion.Task.Result.IsDeliveryUncertain
                                ? SessionState.Ambiguous : SessionState.Idle);
                    }
                }
            }

            context.Phase = OpenCodeTurnPhase.Completed;
            context.Dispose();
        }
    }

    public async Task<bool> CancelTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        TurnContext? context;

        lock (_gate)
        {
            context = _sessions.TryGetValue(sessionId, out var tracked) ? tracked.ActiveTurn : null;
        }

        if (context is null)
        {
            // There is no owned turn to cancel or terminal event to confirm. A native
            // HTTP acknowledgement must not stand in for confirmed cancellation.
            return false;
        }

        context.RequestCancellation();
        // RunTurnAsync owns the single abort/confirmation budget, including the abort request.
        var result = await context.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return string.Equals(result.Status, TurnResult.CancelledStatus, StringComparison.Ordinal);
    }

    public async Task<OpenCodeSessionResponse> ResetSessionAsync(
        string oldSessionId,
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSessionId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        TrackedSession? previous;
        string? directory;

        lock (_gate)
        {
            previous = _sessions.TryGetValue(oldSessionId, out var tracked) ? tracked : null;
            directory = previous?.Response.Directory ?? request.Directory;
            if (previous is not null && !string.IsNullOrWhiteSpace(directory)
                && !string.IsNullOrWhiteSpace(request.Directory)
                && ProjectLock.CanonicalizeRoot(directory) != ProjectLock.CanonicalizeRoot(request.Directory))
                throw new InvalidOperationException("Reset must preserve the confirmed native session directory.");

            if (previous is not null)
            {
                EnsureSessionInteractive(previous);

                if (previous.ActiveTurn is not null)
                {
                    throw new InvalidOperationException(
                        $"Session '{oldSessionId}' has an active turn and cannot be reset.");
                }

                previous.OperationGeneration++;
                previous.IsResetting = true;
            }
        }

        // The directory checked for locks must be the directory sent to native creation,
        // including a caller that omitted it on an already confirmed session.
        request = request with { Directory = directory };

        OpenCodeSessionResponse created;

        try
        {
            await EnsureCheckoutUnlockedAsync(directory, cancellationToken).ConfigureAwait(false);

            created = await _client.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);

            EnsureNativeSessionId(created);
        }
        catch
        {
            if (previous is not null)
            {
                lock (_gate)
                {
                    previous.IsResetting = false;
                }
            }

            throw;
        }

        var resetSession = created with { ParentId = oldSessionId };

        lock (_gate)
        {
            if (previous is not null)
            {
                previous.IsResetting = false;
                previous.State = Transition(previous.State, SessionState.Closed);
            }

            var ancestry = previous is null
                ? new List<string>()
                : new List<string>(previous.Ancestry);

            ancestry.Add(oldSessionId);

            _sessions[created.Id] = new TrackedSession(
                resetSession,
                request.Model,
                request.Agent,
                ancestry);
        }

        return resetSession;
    }

    public IReadOnlyList<string> GetAncestry(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            return GetRequiredTrackedSession(sessionId).Ancestry.ToArray();
        }
    }

    private async Task<TurnResult> RunTurnAsync(
        string sessionId,
        OpenCodePromptRequest request,
        TurnContext context,
        CancellationToken cancellationToken)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.CancelSignal.Token);
        var connected = false;
        await using var budgetTimer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            turnCts, _options.ConnectionTimeout, Timeout.InfiniteTimeSpan);

        try
        {
            await using var events = _client.SubscribeEventsAsync(sessionId, turnCts.Token)
                .GetAsyncEnumerator(turnCts.Token);
            while (await events.MoveNextAsync().ConfigureAwait(false))
            {
                if (events.Current.Type == "server.connected")
                {
                    connected = true;
                    break;
                }

                // A non-conforming/stale bus must not consume a terminal event and then start a job.
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.FailedStatus,
                    "The event stream did not begin with server.connected; the prompt was not sent."));
            }

            if (!connected)
            {
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.FailedStatus,
                    "The event stream ended before the connection was confirmed; the prompt was not sent."));
            }

            // The instance bus is live before prompt_async can publish a fast terminal result.
            turnCts.Token.ThrowIfCancellationRequested();
            budgetTimer.Change(_options.TurnTimeout, Timeout.InfiniteTimeSpan);
            turnCts.Token.ThrowIfCancellationRequested();
            if (!context.TryMarkPromptAttempted())
            {
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.CancelledStatus, errorMessage: null));
            }
            var delivered = await _client.SendPromptAsync(sessionId, request, turnCts.Token).ConfigureAwait(false);
            if (!delivered)
            {
                context.PromptRejected = true;
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.FailedStatus,
                    $"The prompt was not accepted by the native session '{sessionId}'."));
            }

            while (await events.MoveNextAsync().ConfigureAwait(false))
            {
                var envelope = events.Current;
                if (!TrackPermissionEvent(envelope, sessionId, context))
                    return Complete(context, CreateResult(sessionId, context.Accumulator, TurnResult.FailedStatus,
                        "BufferOverflow: the bounded native permission history is full; no request was automatically approved.")
                        with { FinishReason = "BufferOverflow" });
                if (!context.Accumulator.TryApply(envelope, sessionId))
                {
                    continue;
                }

                if (context.Phase == OpenCodeTurnPhase.Cancelling
                    || context.CancelSignal.IsCancellationRequested)
                {
                    // A session-only error can end this iterator without proving that this
                    // prompt stopped. It must use the same abort/terminal confirmation budget
                    // as cancellation exceptions; an already bound terminal remains confirmed.
                    if (!context.Accumulator.TerminalObserved)
                        return await HandleCancellationAsync(sessionId, context).ConfigureAwait(false);
                    return Complete(context, CreateResult(
                        sessionId,
                        context.Accumulator,
                        TurnResult.CancelledStatus,
                        errorMessage: null));
                }

                if (context.Accumulator.ErrorMessage is { Length: > 0 } errorMessage)
                {
                    return Complete(context, CreateResult(
                        sessionId,
                        context.Accumulator,
                        TurnResult.FailedStatus,
                        errorMessage));
                }

                return Complete(context, CreateResult(
                    sessionId,
                    context.Accumulator,
                    TurnResult.CompletedStatus,
                    errorMessage: null));
            }

            return Complete(context, CreateResult(
                sessionId,
                context.Accumulator,
                TurnResult.FailedStatus,
                "The event stream ended before a terminal event was observed."));
        }
        catch (TurnResourceLimitException)
        {
            return Complete(context, CreateResult(sessionId, context.Accumulator, TurnResult.FailedStatus,
                "BufferOverflow: the bounded native turn response is full; delivery remains unconfirmed.")
                with { FinishReason = "BufferOverflow" });
        }
        catch (OpenCodeClientException exception)
        {
            return Complete(context, CreateResult(
                sessionId, context.Accumulator, TurnResult.FailedStatus, exception.Message));
        }
        catch (OperationCanceledException) when (!turnCts.IsCancellationRequested)
        {
            return Complete(context, CreateResult(
                sessionId, context.Accumulator, TurnResult.FailedStatus,
                context.HasPromptAttempted
                    ? "The native transport cancelled the request; prompt delivery is unconfirmed."
                    : "The native transport cancelled before the prompt attempt; the prompt was not sent."));
        }
        catch (OperationCanceledException)
            when (context.CancelSignal.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return await HandleCancellationAsync(sessionId, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Complete(context, CreateResult(
                sessionId,
                context.Accumulator,
                TurnResult.FailedStatus,
                context.HasPromptAttempted
                    ? $"The turn did not complete within {_options.TurnTimeout}."
                    : connected
                        ? "The connection budget expired before the prompt attempt; the prompt was not sent."
                        : $"The event stream was not confirmed within {_options.ConnectionTimeout}; the prompt was not sent.") with { WasTimedOut = true });
        }
    }

    private async Task<TurnResult> HandleCancellationAsync(string sessionId, TurnContext context)
    {
        context.Phase = OpenCodeTurnPhase.Cancelling;

        if (!context.HasPromptAttempted)
        {
            return Complete(context, CreateResult(
                sessionId, context.Accumulator, TurnResult.CancelledStatus, errorMessage: null));
        }

        using var graceCts = new CancellationTokenSource(_options.CancellationTimeout, _timeProvider);

        try
        {
            // The instance bus does not replay events published while no subscriber is connected.
            // Confirm the replacement subscription before abort can publish a fast terminal event.
            await using var events = _client.SubscribeEventsAsync(sessionId, graceCts.Token)
                .GetAsyncEnumerator(graceCts.Token);
            if (!await events.MoveNextAsync().ConfigureAwait(false)
                || events.Current.Type != "server.connected")
            {
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.FailedStatus,
                    "The cancellation event stream was not confirmed; cancellation is unconfirmed."));
            }

            try
            {
                await EnsureAbortSentAsync(sessionId, context, graceCts.Token).ConfigureAwait(false);
            }
            catch (OpenCodeClientException)
            {
            }
            catch (OperationCanceledException) when (graceCts.IsCancellationRequested)
            {
                return Complete(context, CreateResult(
                    sessionId, context.Accumulator, TurnResult.FailedStatus,
                    "The native abort request exceeded the cancellation budget; cancellation is unconfirmed."));
            }

            while (await events.MoveNextAsync().ConfigureAwait(false))
            {
                if (!TrackPermissionEvent(events.Current, sessionId, context))
                    return Complete(context, CreateResult(sessionId, context.Accumulator, TurnResult.FailedStatus,
                        "BufferOverflow: the bounded native permission history is full; no request was automatically approved.")
                        with { FinishReason = "BufferOverflow", IsDeliveryUncertain = true });
                if (context.Accumulator.TryApply(events.Current, sessionId) && context.Accumulator.TerminalObserved)
                {
                    return Complete(context, CreateResult(
                        sessionId,
                        context.Accumulator,
                        TurnResult.CancelledStatus,
                        errorMessage: null));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (OpenCodeClientException)
        {
        }

        return Complete(context, CreateResult(
            sessionId,
            context.Accumulator,
            TurnResult.FailedStatus,
            $"Cancellation of session '{sessionId}' was not confirmed by a terminal event within " +
            $"{_options.CancellationTimeout}."));
    }

    private async Task EnsureAbortSentAsync(
        string sessionId,
        TurnContext context,
        CancellationToken cancellationToken)
    {
        if (context.IsAbortSent)
        {
            return;
        }

        await _client.AbortSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        context.MarkAbortSent();
    }

    private async Task EnsureCheckoutUnlockedAsync(string? rootPath, CancellationToken cancellationToken)
    {
        if (_projectLockRepository is null || string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var activeLock = await _projectLockRepository
            .GetActiveByRootPathAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);

        if (activeLock is null || !activeLock.IsHeld)
        {
            return;
        }

        throw new InvalidOperationException("Checkout has an active writer lock; reset cannot release another execution's ownership.");
    }

    private static void EnsurePromptMatchesConfirmedBinding(
        TrackedSession tracked,
        OpenCodePromptRequest request)
    {
        var confirmedModel = tracked.Binding?.ModelId ?? tracked.RequestedModel ?? tracked.Response.Model;

        if (!string.IsNullOrWhiteSpace(request.Model)
            && !string.IsNullOrWhiteSpace(confirmedModel)
            && !string.Equals(request.Model, confirmedModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The turn model '{request.Model}' does not match the confirmed immutable binding " +
                $"'{confirmedModel}'.");
        }

        if (!string.IsNullOrWhiteSpace(request.Agent)
            && !string.IsNullOrWhiteSpace(tracked.RequestedAgent)
            && !string.Equals(request.Agent, tracked.RequestedAgent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The turn agent '{request.Agent}' does not match the confirmed immutable binding " +
                $"'{tracked.RequestedAgent}'.");
        }
    }

    private static TurnResult CreateResult(
        string sessionId,
        TurnAccumulator accumulator,
        string status,
        string? errorMessage)
    {
        return new TurnResult
        {
            SessionId = sessionId,
            OutputText = accumulator.OutputText,
            Status = status,
            FinishReason = accumulator.FinishReason,
            Tokens = accumulator.Tokens,
            ToolCalls = accumulator.ToolCalls,
            ErrorMessage = errorMessage,
            ObservedModelId = accumulator.ObservedModelId,
            ObservedProviderId = accumulator.ObservedProviderId
        };
    }

    private TurnResult Complete(TurnContext context, TurnResult result)
    {
        lock (_gate)
        {
            // Every terminal path, including cancellation and native failure, retains ownership
            // when native permission requests have not been reconciled.
            if (context.HasPromptAttempted && !context.PromptRejected && context.Accumulator.TerminalObserved
                && context.Permissions.Values.Any(p => !p.Resolved))
                result = result with { Status = TurnResult.FailedStatus, IsDeliveryUncertain = true,
                    ErrorMessage = "Native terminal contradicted an unresolved permission request; ownership must be reconciled." };
            if (context.HasPromptAttempted && !context.PromptRejected && !context.Accumulator.TerminalObserved)
                result = result with { IsDeliveryUncertain = true };
            context.Completion.TrySetResult(result);
        }

        return result;
    }

    private static SessionState Transition(SessionState current, SessionState target)
    {
        if (!SessionStateMachine.AllowedTransitions.Contains((current, target)))
        {
            throw new InvalidOperationException(
                $"Session state transition '{current}' -> '{target}' is not allowed.");
        }

        return target;
    }

    private static void EnsureSessionOpen(TrackedSession tracked)
    {
        if (tracked.State == SessionState.Closed)
        {
            throw new InvalidOperationException(
                $"Session '{tracked.Response.Id}' is closed; reset creates a new native session.");
        }
    }

    private static void EnsureSessionInteractive(TrackedSession tracked)
    {
        EnsureSessionOpen(tracked);

        if (tracked.State is SessionState.Ambiguous or SessionState.Orphaned)
            throw new InvalidOperationException("Native outcome is unconfirmed; reconcile the session before sending or resetting.");

        if (tracked.IsResetting)
        {
            throw new InvalidOperationException(
                $"Session '{tracked.Response.Id}' is being reset and cannot accept new work.");
        }
    }

    private static void EnsureNativeSessionId(OpenCodeSessionResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Id))
        {
            throw new OpenCodeClientException(
                "The OpenCode session response does not confirm a native session identifier.");
        }
    }

    private TrackedSession GetRequiredTrackedSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var tracked))
        {
            throw new InvalidOperationException(
                $"The native session '{sessionId}' is not tracked by this lifecycle service.");
        }

        return tracked;
    }

    private sealed class TrackedSession
    {
        public TrackedSession(
            OpenCodeSessionResponse response,
            string? requestedModel,
            string? requestedAgent,
            IReadOnlyList<string> ancestry)
        {
            Response = response;
            RequestedModel = requestedModel;
            RequestedAgent = requestedAgent;
            Ancestry = ancestry;

            State = SessionState.Draft;
            State = Transition(State, SessionState.Starting);
            State = Transition(State, SessionState.Active);
        }

        public OpenCodeSessionResponse Response { get; set; }

        public SessionState State { get; set; }

        public SessionBinding? Binding { get; set; }

        public string? RequestedModel { get; }

        public string? RequestedAgent { get; }

        public IReadOnlyList<string> Ancestry { get; }

        public bool IsResetting { get; set; }
        public bool ContinuationPending { get; set; }
        public long OperationGeneration { get; set; }

        public TurnContext? ActiveTurn { get; set; }

        public HashSet<string> PromptMessageIds { get; } = new(StringComparer.Ordinal);
    }

    private enum OpenCodeTurnPhase
    {
        Idle,
        Running,
        Cancelling,
        Completed
    }

    private sealed class TurnContext : IDisposable
    {
        public TurnContext(string promptMessageId) => Accumulator = new TurnAccumulator(promptMessageId);
        public Dictionary<string, PendingPermissionState> Permissions { get; } = new(StringComparer.Ordinal);
        public bool PromptRejected { get; set; }
        private int _abortSent;
        // 0 = not attempted, 1 = attempted (delivery may be ambiguous), 2 = cancelled before attempt.
        private int _deliveryState;

        public OpenCodeTurnPhase Phase { get; set; } = OpenCodeTurnPhase.Idle;

        public CancellationTokenSource CancelSignal { get; } = new();

        public TaskCompletionSource<TurnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TurnAccumulator Accumulator { get; }

        public bool IsAbortSent => Volatile.Read(ref _abortSent) == 1;

        public bool HasPromptAttempted => Volatile.Read(ref _deliveryState) == 1;

        public bool TryMarkPromptAttempted() => Interlocked.CompareExchange(ref _deliveryState, 1, 0) == 0;

        public void RequestCancellation()
        {
            Interlocked.CompareExchange(ref _deliveryState, 2, 0);
            try
            {
                CancelSignal.Cancel();
            }
            catch (ObjectDisposedException) when (Completion.Task.IsCompleted)
            {
                // The terminal result won the race; there is no remaining turn to signal.
            }
        }

        public void MarkAbortSent()
        {
            Interlocked.Exchange(ref _abortSent, 1);
        }

        public void Dispose()
        {
            CancelSignal.Dispose();
        }
    }

    private sealed class TurnResourceLimitException : Exception { }

    private sealed class TurnAccumulator
    {
        private const long MaximumContentCharacters = 4L * 1024 * 1024;
        private const int MaximumRecords = 4096;
        private const int MaximumIdentifierCharacters = 4096;
        private long _contentCharacters;
        private readonly string _promptMessageId;
        private bool _unboundError;
        public TurnAccumulator(string promptMessageId) => _promptMessageId = promptMessageId;
        public bool TerminalObserved { get; private set; }
        private static readonly string[] SessionContainers = { "", "part", "info" };
        private readonly Dictionary<string, string> _textByPartId = new(StringComparer.Ordinal);
        private readonly List<string> _textOrder = new();
        private readonly Dictionary<string, ToolCallInfo> _toolCallsByKey = new(StringComparer.Ordinal);
        private readonly List<string> _toolOrder = new();
        private readonly Dictionary<string, string> _messageRoles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _partMessages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _toolMessages = new(StringComparer.Ordinal);

        public string? FinishReason { get; private set; }

        public string? ObservedModelId { get; private set; }
        public string? ObservedProviderId { get; private set; }

        private bool _observedModelConflict;

        public OpenCodeTokenUsage? Tokens { get; private set; }

        public string? ErrorMessage { get; private set; }

        public string OutputText =>
            string.Join(TextSeparator, _textOrder.Where(partId => IsBoundAssistant(_partMessages[partId]))
                .Select(partId => _textByPartId[partId]));

        public IReadOnlyList<ToolCallInfo> ToolCalls =>
            _toolOrder.Where(key => IsBoundAssistant(_toolMessages[key]))
                .Select(key => _toolCallsByKey[key]).ToArray();

        public bool TryApply(OpenCodeEventEnvelope envelope, string expectedSessionId)
        {
            var terminal = ApplyEnvelope(envelope, expectedSessionId);
            TerminalObserved |= terminal && !_unboundError;
            return terminal;
        }

        private bool ApplyEnvelope(OpenCodeEventEnvelope envelope, string expectedSessionId)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            if (!IsExpectedSession(GetEnvelopeSessionId(envelope), expectedSessionId))
            {
                return false;
            }

            switch (envelope.Type)
            {
                case MessagePartUpdatedEvent.EventType:
                    return ApplyPart(envelope, expectedSessionId);

                case "message.part.delta":
                    ApplyTextDelta(envelope);
                    return false;

                case SessionIdleEvent.EventType:
                    // Idle identifies a session, never the prompt that became idle. Buffered
                    // idle from an older turn cannot confirm this turn or its cancellation.
                    return false;

                case "session.error":
                case "error":
                    if (!IsExpectedSession(GetEnvelopeSessionId(envelope), expectedSessionId))
                    {
                        return false;
                    }

                    var nativeMessage = ExtractErrorMessage(envelope);
                    _unboundError = true;
                    SetErrorMessage(string.IsNullOrWhiteSpace(nativeMessage)
                        ? "The native session reported an error without a supported message."
                        : nativeMessage);

                    return true;

                case MessageUpdatedEvent.EventType:
                    return ApplyMessage(envelope);

                default:
                    return false;
            }
        }

        private bool ApplyPart(OpenCodeEventEnvelope envelope, string expectedSessionId)
        {
            if (!MessagePartUpdatedEvent.TryParse(envelope, out var part) || part is null)
            {
                return false;
            }

            // TryApply validated every reported identity before any mutation. A missing nested
            // ID may use that concrete envelope identity; empty/malformed/conflicting IDs fail there.
            var sessionId = string.IsNullOrEmpty(part.SessionId)
                ? GetEnvelopeSessionId(envelope)
                : part.SessionId;
            if (!IsExpectedSession(sessionId, expectedSessionId))
            {
                return false;
            }

            if (!IsBoundAssistant(part.MessageId)) return false;

            if (string.Equals(part.PartType, MessagePartUpdatedEvent.TextPartType, StringComparison.Ordinal))
            {
                if (part.Text is null)
                {
                    return false;
                }

                var newPart = !_textByPartId.ContainsKey(part.PartId);
                if (newPart) RequireRecordRoom(_textByPartId.Count);
                RequireIdentifier(part.PartId);
                RequireIdentifier(part.MessageId);
                ReplaceContentCharacters(_textByPartId.GetValueOrDefault(part.PartId)?.Length ?? 0, part.Text.Length);
                if (newPart) _textOrder.Add(part.PartId);
                _textByPartId[part.PartId] = part.Text;
                _partMessages[part.PartId] = part.MessageId;

                return false;
            }

            if (string.Equals(part.PartType, MessagePartUpdatedEvent.ToolPartType, StringComparison.Ordinal))
            {
                var key = string.IsNullOrEmpty(part.CallId) ? part.PartId : part.CallId;
                RequireIdentifier(key);
                RequireIdentifier(part.MessageId);

                var newTool = !_toolCallsByKey.ContainsKey(key);
                if (newTool) RequireRecordRoom(_toolCallsByKey.Count);
                var toolCall = new ToolCallInfo
                {
                    CallId = key,
                    Tool = part.Tool ?? string.Empty,
                    Status = part.ToolStatus,
                    Input = part.ToolInput,
                    Output = part.ToolOutput
                };
                ReplaceContentCharacters(_toolCallsByKey.TryGetValue(key, out var existing) ? ToolCharacters(existing) : 0,
                    ToolCharacters(toolCall));
                if (newTool) _toolOrder.Add(key);
                _toolCallsByKey[key] = toolCall;
                _toolMessages[key] = part.MessageId;

                return false;
            }

            if (string.Equals(part.PartType, MessagePartUpdatedEvent.StepFinishPartType, StringComparison.Ordinal))
            {
                SetFinishReason(part.Reason);
                Tokens ??= part.Tokens;

                // Tool steps and unknown finish reasons do not prove the native job stopped.
                return part.Reason is "stop" or "end_turn";
            }

            return false;
        }

        private bool ApplyMessage(OpenCodeEventEnvelope envelope)
        {
            if (!MessageUpdatedEvent.TryParse(envelope, out var message) || message is null)
            {
                return false;
            }

            if (message.Role != "assistant" || message.ParentId != _promptMessageId)
                return false;

            NoteObservedModel(message.ModelId, message.ProviderId, message.ModelMetadataInvalid);

            RequireIdentifier(message.MessageId);
            if (!_messageRoles.ContainsKey(message.MessageId)) RequireRecordRoom(_messageRoles.Count);
            _messageRoles[message.MessageId] = message.Role;

            if (message.Role == "assistant" && envelope.Properties.TryGetProperty("info", out var info))
                if (ErrorMessage is null) SetErrorMessage(ExtractErrorMessage(info, allowBareMessage: false));

            if (string.Equals(message.Role, "assistant", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(message.Finish))
            {
                SetFinishReason(message.Finish);
            }
            return ErrorMessage is not null || message.CompletedAtUnixMilliseconds is not null
                && message.Finish is "stop" or "end_turn";
        }

        private void NoteObservedModel(string? modelId, string? providerId, bool invalid)
        {
            if (_observedModelConflict) return;
            if (invalid || modelId is not null && ObservedModelId is not null && ObservedModelId != modelId
                || providerId is not null && ObservedProviderId is not null && ObservedProviderId != providerId)
            {
                ObservedModelId = null;
                ObservedProviderId = null;
                _observedModelConflict = true;
                return;
            }
            if (modelId is null) return;
            ObservedModelId ??= modelId;
            // Provider-only events cannot be combined with a model from another event.
            if (providerId is not null) ObservedProviderId ??= providerId;
        }

        private bool IsBoundAssistant(string messageId) =>
            _messageRoles.TryGetValue(messageId, out var role) && role == "assistant";

        private void ApplyTextDelta(OpenCodeEventEnvelope envelope)
        {
            var properties = envelope.Properties;
            if (properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String || field.GetString() != "text"
                || !properties.TryGetProperty("partID", out var part) || part.ValueKind != JsonValueKind.String
                || !properties.TryGetProperty("messageID", out var message) || message.ValueKind != JsonValueKind.String
                || !properties.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.String) return;
            var partId = part.GetString()!;
            var messageId = message.GetString()!;
            if (_partMessages.TryGetValue(partId, out var owner) && owner == messageId
                && IsBoundAssistant(messageId))
            {
                var text = delta.GetString()!;
                ReplaceContentCharacters(0, text.Length);
                _textByPartId[partId] += text;
            }
        }

        private void SetErrorMessage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ReplaceContentCharacters(ErrorMessage?.Length ?? 0, value.Length);
            ErrorMessage = value;
        }

        private void SetFinishReason(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            RequireIdentifier(value);
            FinishReason = value;
        }

        private static void RequireRecordRoom(int count)
        {
            if (count >= MaximumRecords) throw new TurnResourceLimitException();
        }
        private static void RequireIdentifier(string value)
        {
            if (value.Length > MaximumIdentifierCharacters) throw new TurnResourceLimitException();
        }
        private void ReplaceContentCharacters(long previous, long next)
        {
            if (next > MaximumContentCharacters - (_contentCharacters - previous)) throw new TurnResourceLimitException();
            _contentCharacters += next - previous;
        }
        private static long ToolCharacters(ToolCallInfo tool) => (long)tool.CallId.Length + tool.Tool.Length
            + (tool.Status?.Length ?? 0) + JsonCharacters(tool.Input) + JsonCharacters(tool.Output);
        private static int JsonCharacters(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? 0 : element.GetRawText().Length;

        private static bool IsExpectedSession(string? sessionId, string expectedSessionId)
        {
            return !string.IsNullOrEmpty(sessionId)
                && string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal);
        }

        private static string? GetEnvelopeSessionId(OpenCodeEventEnvelope envelope)
        {
            string? observed = null;
            var properties = envelope.Properties;
            foreach (var name in SessionContainers)
            {
                var candidate = properties;
                if (name.Length > 0
                    && (properties.ValueKind != JsonValueKind.Object
                        || !properties.TryGetProperty(name, out candidate)))
                {
                    continue;
                }

                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("sessionID", out var value))
                {
                    continue;
                }

                if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
                {
                    return null;
                }

                var id = value.GetString();
                if (observed is not null && !string.Equals(observed, id, StringComparison.Ordinal))
                {
                    return null;
                }

                observed = id;
            }

            return observed;
        }

        private static string? ExtractErrorMessage(OpenCodeEventEnvelope envelope)
            => ExtractErrorMessage(envelope.Properties);

        private static string? ExtractErrorMessage(JsonElement properties, bool allowBareMessage = true)
        {

            if (properties.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (properties.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var errorMessage)
                    && errorMessage.ValueKind == JsonValueKind.String)
                {
                    return errorMessage.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var dataMessage)
                    && dataMessage.ValueKind == JsonValueKind.String) return dataMessage.GetString();
            }

            if (allowBareMessage && properties.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            return null;
        }
    }
}
