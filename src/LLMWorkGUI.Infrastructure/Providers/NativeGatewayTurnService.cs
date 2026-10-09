using System.Collections.Concurrent;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Single-attempt gateway execution. Local sessions are never presented as native continuations.</summary>
public sealed class NativeGatewayTurnService : INativeGatewayTurnService
{
    private readonly Func<ILlmGateway> _gatewayFactory;
    private readonly IApplicationInstanceGuard _guard;
    private readonly ICheckoutLockService _locks;
    private readonly IHealthCenterService _health;
    private readonly SensitiveDataFilter _filter;
    private readonly SqliteNativeGatewayJournal _journal;
    private readonly NativeGatewayEgressCoordinator? _egress;
    // Retain mutex ownership until explicit reconciliation; Dispose must not release uncertain work.
    private readonly ConcurrentDictionary<string, ICheckoutLockToken> _retained = new();

    public NativeGatewayTurnService(Func<ILlmGateway> gatewayFactory, ISqliteConnectionFactory factory,
        IApplicationInstanceGuard guard, ICheckoutLockService locks, IHealthCenterService health,
        SensitiveDataFilter filter, TimeProvider clock, IActivityCenterService? activity = null,
        NativeGatewayEgressCoordinator? egress = null)
    {
        _gatewayFactory = gatewayFactory; _guard = guard; _locks = locks; _health = health; _filter = filter;
        _journal = new(factory, guard, clock, activity);
        _egress = egress;
    }

    public Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            foreach (var value in new[] { request.ProjectId, request.RootPath, request.RouteId, request.ClientRequestId, request.Prompt })
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(request));
            _guard.EnsureSupervisorPermitted();
        }
        catch
        {
            if (request.EgressPreviewId is { } id) _egress?.Revoke(id);
            throw;
        }
        // Includes lazy native account-store construction, so no synchronous native I/O runs on WPF.
        return ExecuteQueuedAsync(request, cancellationToken);
    }

    private async Task<NativeGatewayTurnResult> ExecuteQueuedAsync(NativeGatewayTurnRequest request, CancellationToken token)
    {
        try { return await Task.Run(() => ExecuteCoreAsync(request, token), token).ConfigureAwait(false); }
        finally { if (request.EgressPreviewId is { } id) _egress?.Revoke(id); }
    }

    private async Task<NativeGatewayTurnResult> ExecuteCoreAsync(NativeGatewayTurnRequest request, CancellationToken callerToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(request.Timeout);
        var executionToken = deadline.Token;
        var authorization = request.EgressPreviewId is null ? null
            : await (_egress ?? throw new LLMWorkGUI.Application.Security.EgressApprovalException()).ConsumeAsync(request, executionToken).ConfigureAwait(false);
        var effectiveRequest = authorization is null ? request : request with { Prompt = authorization.Prompt };
        var entry = await _journal.BeginAsync(effectiveRequest, executionToken, authorization).ConfigureAwait(false);
        authorization?.Attach(entry, _journal);
        ICheckoutLockToken? lockToken = null;
        var observation = new StreamObservation();
        NativeGatewayDispatchAuthorization? dispatchAuthorization = null;
        Task<ChatResult>? stream = null;
        ExecutionState state;
        ExecutionFailureReason failure;
        HealthErrorClass? healthError = null;
        string? content = null;
        try
        {
            lockToken = await _locks.AcquireWriterLockAsync(request.ProjectId, entry.Context.WorkingDirectory,
                entry.ExecutionId, 0, deadline.Token).ConfigureAwait(false);
            _retained[entry.ExecutionId] = lockToken;
            if (authorization is null) await _journal.ValidateDispatchAsync(entry, deadline.Token).ConfigureAwait(false);
            stream = Task.Run(async () =>
            {
                var gateway = authorization?.Gateway ?? _gatewayFactory();
                executionToken.ThrowIfCancellationRequested();
                if (authorization is null)
                {
                    if (gateway is LlmGateway) dispatchAuthorization = new(entry, _journal, lockToken);
                    else await _journal.MarkRunningAsync(entry, executionToken).ConfigureAwait(false);
                }
                var result = await ReadStreamAsync(gateway, entry.Context, effectiveRequest.Prompt, observation, executionToken,
                    (INativeDispatchAuthorization?)authorization ?? dispatchAuthorization).ConfigureAwait(false);
                if (dispatchAuthorization is { TransportAttempted: false }) throw ProtocolError();
                return result;
            }, CancellationToken.None);
            var result = await stream.WaitAsync(deadline.Token).ConfigureAwait(false);
            content = result.Content is null ? null : _filter.Redact(result.Content);
            state = ExecutionState.Succeeded; failure = ExecutionFailureReason.None;
        }
        catch (Exception exception)
        {
            // No terminal native-process contract exists for cancellation/error. Even a completed
            // disposal is not evidence that a remote operation stopped. Keep ownership after dispatch.
            (failure, healthError) = Classify(exception, callerToken.IsCancellationRequested);
            if (stream is { IsCompleted: false })
            {
                deadline.Cancel();
                try { await stream.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { /* Observe late faults without retrying or changing the durable result. */ }
                if (!stream.IsCompleted) _ = stream.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (authorization is { TransportAttempted: false } && stream is { IsCompleted: true }) observation.DispatchPossible = false;
            if (dispatchAuthorization is { TransportAttempted: false } && stream is { IsCompleted: true }) observation.DispatchPossible = false;
            var uncertain = observation.DispatchPossible || stream is { IsCompleted: false };
            // Admission/lock/configuration failures are local, not observations of provider health.
            if (!observation.DispatchPossible) healthError = null;
            state = uncertain ? ExecutionState.Ambiguous : failure switch
            {
                ExecutionFailureReason.UserCancelled => ExecutionState.Cancelled,
                ExecutionFailureReason.NetworkTimeout => ExecutionState.TimedOut,
                _ => ExecutionState.Failed
            };
        }

        // Commit the terminal result before relinquishing either durable or physical
        // ownership. A failed commit leaves the token retained for reconciliation.
        await _journal.CompleteAsync(entry, state, failure).ConfigureAwait(false);
        if (state != ExecutionState.Ambiguous && lockToken is not null)
        {
            await lockToken.ReleaseAsync("NativeGatewayTerminal", CancellationToken.None).ConfigureAwait(false);
            _retained.TryRemove(entry.ExecutionId, out _);
        }
        if (healthError is { } error && error != HealthErrorClass.UserCancellation)
        {
            var scope = error is HealthErrorClass.AuthenticationOrRefresh or HealthErrorClass.QuotaOrRateLimit
                ? HealthScope.ForAccount(entry.AccountId) : HealthScope.ForModelRoute(entry.AccountId, entry.ModelId);
            await _health.ReportFailureAsync(scope, error, "NativeGateway: " + failure, CancellationToken.None).ConfigureAwait(false);
        }
        if (state == ExecutionState.Ambiguous)
            await _health.ReportFailureAsync(HealthScope.ForModelRoute(entry.AccountId, entry.ModelId),
                HealthErrorClass.UnknownOrAmbiguousCompletion, "NativeGateway: требуется reconciliation.", CancellationToken.None).ConfigureAwait(false);
        // A textual reply never upgrades auth, capabilities or response-origin identity.
        return new(entry.SessionId, entry.ExecutionId, state, failure, content, state == ExecutionState.Ambiguous);
    }

    private static async Task<ChatResult> ReadStreamAsync(ILlmGateway gateway, GatewayExecutionContext context,
        string prompt, StreamObservation observation, CancellationToken token, INativeDispatchAuthorization? authorization = null)
    {
        var model = $"{context.Provider.ToString().ToLowerInvariant()}/{context.AccountId}/{context.NativeModel}";
        var request = new ChatRequest { Model = model, AccountId = context.AccountId,
            ExecutionContext = context, ReasoningEffort = context.ReasoningEffort,
            Messages = [ChatMessage.User(prompt)], DispatchAuthorization = authorization };
        ChatResult? result = null;
        var started = false;
        // Only the sealed in-process implementation guarantees that its first update precedes
        // adapter invocation. A remote client can lose a response after dispatch but before Started
        // reaches us; unknown implementations retain the same conservative ownership boundary.
        // This sealed HTTP client rejects the exact in-process binding before sending.
        // Do not turn that deterministic local refusal into an uncertain remote dispatch.
        if (gateway is LLMGateway.Core.Client.OpenAiGatewayClient)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Exact execution binding requires an in-process gateway.");
        observation.DispatchPossible = gateway is not LlmGateway;
        // Read to EOF and await disposal after Completed; failures after the apparent terminal matter.
        await foreach (var update in gateway.StreamAsync(request, token).WithCancellation(token).ConfigureAwait(false))
        {
            // Started means route/slot admission, not native process or response-origin proof.
            // Keep ownership even for malformed first events and any failure after this boundary.
            observation.DispatchPossible = true;
            if (result is not null) throw ProtocolError();
            switch (update.Kind)
            {
                case ChatUpdateKind.Started:
                    if (started || update.AccountId != context.AccountId || update.Provider != context.Provider || update.Model != model)
                        throw ProtocolError();
                    started = true;
                    break;
                case ChatUpdateKind.TextDelta or ChatUpdateKind.ReasoningDelta:
                    if (!started) throw ProtocolError();
                    break;
                case ChatUpdateKind.Completed:
                    result = update.Result;
                    if (!started || result is null || result.AccountId != context.AccountId || result.Provider != context.Provider
                        || result.Model != model || result.NativeModel != context.NativeModel || string.IsNullOrWhiteSpace(result.Content)
                        || result.ToolCalls.Count != 0 || result.FinishReason != "stop") throw ProtocolError();
                    break;
                default: throw ProtocolError();
            }
        }
        return result ?? throw ProtocolError();
    }

    private static InvalidDataException ProtocolError() => new("Неподтверждённая последовательность событий LLMGateway.");
    private static (ExecutionFailureReason, HealthErrorClass) Classify(Exception exception, bool cancelled) => exception switch
    {
        OperationCanceledException => cancelled
            ? (ExecutionFailureReason.UserCancelled, HealthErrorClass.UserCancellation)
            : (ExecutionFailureReason.NetworkTimeout, HealthErrorClass.NetworkOrTimeout),
        GatewayException { Kind: GatewayErrorKind.Timeout } => (ExecutionFailureReason.NetworkTimeout, HealthErrorClass.NetworkOrTimeout),
        GatewayException { Kind: GatewayErrorKind.RateLimited } => (ExecutionFailureReason.QuotaExceeded, HealthErrorClass.QuotaOrRateLimit),
        GatewayException { Kind: GatewayErrorKind.AuthenticationRequired or GatewayErrorKind.Unauthorized } => (ExecutionFailureReason.AuthenticationFailure, HealthErrorClass.AuthenticationOrRefresh),
        GatewayException { Kind: GatewayErrorKind.ProviderUnavailable } => (ExecutionFailureReason.StartupFailure, HealthErrorClass.ExecutableMissingOrVersion),
        GatewayException { Kind: GatewayErrorKind.ModelNotFound or GatewayErrorKind.NotFound } => (ExecutionFailureReason.ModelUnavailable, HealthErrorClass.ModelUnavailableOrMismatch),
        ProjectLockConflictException => (ExecutionFailureReason.WorkspaceConflict, HealthErrorClass.WorkspaceConflict),
        InvalidDataException => (ExecutionFailureReason.MalformedProtocol, HealthErrorClass.MalformedProtocolEvent),
        _ => (ExecutionFailureReason.InternalError, HealthErrorClass.Provider4xx5xx)
    };

    private sealed class StreamObservation { public volatile bool DispatchPossible; }
}
