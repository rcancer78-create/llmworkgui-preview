using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// ACP client baseline: performs the <c>initialize</c> handshake over an injected JSON-RPC stdio
/// transport, sends the numeric <c>protocolVersion: 1</c> and the LLMWorkGUI client info, validates
/// the response through <see cref="CursorAcpHandshakeValidator"/>, and implements the session and
/// turn operations confirmed by the sanitized Phase 6 fixtures: <c>session/new</c>,
/// <c>session/load</c>, <c>session/prompt</c>, <c>session/update</c> streaming normalization,
/// <c>session/request_permission</c> replies and <c>session/cancel</c>. Every expected failure is
/// mapped to a typed degraded result (never an unhandled exception); caller cancellation is the
/// only exception that propagates.
/// </summary>
public sealed partial class CursorAcpClient : ICursorAcpClient
{
    public bool ReportsPromptDispatch => true;
    public const string InitializeMethod = "initialize";

    public const string NewSessionMethod = "session/new";

    public const string LoadSessionMethod = "session/load";

    public const string PromptMethod = "session/prompt";

    public const string SetModeMethod = "session/set_mode";

    public const string CancelMethod = "session/cancel";

    public const string SessionUpdateMethod = "session/update";

    public const string RequestPermissionMethod = "session/request_permission";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IJsonRpcTransport _transport;
    private readonly CursorAcpOptions _options;
    private readonly CursorAcpHandshakeValidator _validator;
    private readonly ILogger<CursorAcpClient> _logger;

    private long _skippedStreamEventCount;
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public CursorAcpClient(
        IJsonRpcTransport transport,
        CursorAcpOptions? options = null,
        CursorAcpHandshakeValidator? validator = null,
        ILogger<CursorAcpClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _options = options ?? new CursorAcpOptions();
        _options.Validate();
        _validator = validator ?? new CursorAcpHandshakeValidator();
        _logger = logger ?? NullLogger<CursorAcpClient>.Instance;
    }

    public CursorAcpHandshakeResult? CurrentReadiness { get; private set; }

    public long SkippedStreamEventCount => Interlocked.Read(ref _skippedStreamEventCount);

    public async Task<CursorAcpHandshakeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        CurrentReadiness = result;
        return result;
    }

    public async Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentReadiness is not { IsReady: true })
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.NotReady,
                "The ACP session/new call requires a successful initialize handshake first; the current " +
                "readiness is not ready.",
                CursorAcpPolicy.SessionNotReadyGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
            !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.InvalidWorkingDirectory,
                "The ACP session/new call requires 'cwd' to be a non-empty fully-qualified directory path; " +
                $"received '{request.WorkingDirectory}'.",
                CursorAcpPolicy.InvalidWorkingDirectoryGuidance);
        }

        return await SendSessionRequestAsync(
                NewSessionMethod,
                BuildNewSessionParameters(request),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentReadiness is not { IsReady: true } readiness)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.NotReady,
                "The ACP session/load call requires a successful initialize handshake first; the current " +
                "readiness is not ready.",
                CursorAcpPolicy.SessionNotReadyGuidance);
        }

        if (!readiness.Evidence!.AgentCapabilities.LoadSession)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.UnsupportedCapability,
                "The ACP session/load call requires agentCapabilities.loadSession=true; the agent reported " +
                "the capability as absent or false, so no request was sent.",
                CursorAcpPolicy.SessionLoadUnsupportedGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.InvalidSessionId,
                "The ACP session/load call requires a non-empty 'sessionId'; the persisted native session " +
                "identifier was empty.",
                CursorAcpPolicy.SessionLoadGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
            !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.InvalidWorkingDirectory,
                "The ACP session/load call requires 'cwd' to be a non-empty fully-qualified directory path; " +
                $"received '{request.WorkingDirectory}'.",
                CursorAcpPolicy.InvalidWorkingDirectoryGuidance);
        }

        return await SendSessionRequestAsync(
                LoadSessionMethod,
                BuildLoadSessionParameters(request),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CursorAcpPromptResult> PromptAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Keep mode selection and its prompt together, including calls outside the turn supervisor.
        var observer = request.DispatchObserver;
        var dispatchObserved = 0;
        request = request with
        {
            DispatchObserver = attempted =>
            {
                if (Interlocked.Exchange(ref dispatchObserved, 1) == 0) observer?.Invoke(attempted);
            }
        };
        if (!await _promptGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            request.DispatchObserver?.Invoke(false);
            return CursorAcpPromptResult.Degraded(CursorAcpPromptFailureKind.PromptBusy,
                "Another ACP prompt is active on this client; no mode change or prompt was sent.");
        }
        try
        {
            lock (_nativePermissionSync) _cancelledPermissionSessions.Remove(request.SessionId);
            return await PromptCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(_options.RequestTimeout);
                await CancelNativePermissionsAsync(request.SessionId, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not close pending native permissions after the ACP prompt.");
            }
            _promptGate.Release();
            request.DispatchObserver?.Invoke(false);
        }
    }

    private async Task<CursorAcpPromptResult> PromptCoreAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentReadiness is not { IsReady: true })
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.NotReady,
                "The ACP session/prompt call requires a successful initialize handshake first; the current " +
                "readiness is not ready.",
                CursorAcpPolicy.PromptGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.InvalidSessionId,
                "The ACP session/prompt call requires a non-empty 'sessionId'.",
                CursorAcpPolicy.PromptGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.InvalidPrompt,
                "The ACP session/prompt call requires a non-empty prompt text.",
                CursorAcpPolicy.PromptGuidance);
        }

        if (!Guid.TryParse(request.ClientRequestId, out _)
            || !string.Equals(request.PromptHash, CursorAcpPromptRequest.ComputePromptHash(request.Prompt), StringComparison.Ordinal))
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.InvalidRequest,
                "The ACP session/prompt call requires a UUID clientRequestId and the canonical hash of its prompt bytes.",
                CursorAcpPolicy.PromptGuidance);
        }

        if (request.AuthorizeDispatchAsync is not null && _transport is not IJsonRpcWriteGateTransport)
            return CursorAcpPromptResult.Degraded(CursorAcpPromptFailureKind.TransportFailure,
                "The transport cannot enforce project authorization at the prompt write boundary; no prompt was sent.");

        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.BeforeDispatchCancellationToken);
        try
        {
            preparation.Token.ThrowIfCancellationRequested();
            if (request.RequireModelAcknowledgement)
            {
                var modelFailure = await SelectPromptModelAsync(request, preparation.Token).ConfigureAwait(false);
                if (modelFailure is not null) return modelFailure;
            }
            if (request.ModeId is not null)
            {
                var modeFailure = await SelectPromptModeAsync(request, preparation.Token).ConfigureAwait(false);
                if (modeFailure is not null)
                    return modeFailure;
            }
            preparation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (request.BeforeDispatchCancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CursorAcpPromptResult.Degraded(CursorAcpPromptFailureKind.CancelledBeforeDispatch,
                "The caller cancelled ACP preparation; no prompt was sent.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        JsonRpcResponse response;

        try
        {
            Task<JsonRpcResponse> pending;
            if (request.AuthorizeDispatchAsync is { } authorize && _transport is IJsonRpcWriteGateTransport gated)
            {
                var parameters = BuildPromptParameters(request);
                pending = gated.SendRequestWithGateAsync(PromptMethod, parameters,
                    (actual, token) => string.Equals(actual.GetRawText(), parameters.GetRawText(), StringComparison.Ordinal)
                        ? authorize(request, token) : Task.FromResult(false),
                    () => request.DispatchObserver?.Invoke(true), _options.TurnHardTimeout,
                    request.BeforeDispatchCancellationToken, cancellationToken);
            }
            else
            {
                pending = _transport.SendRequestAsync(
                    PromptMethod,
                    BuildPromptParameters(request),
                    _options.TurnHardTimeout,
                    cancellationToken);
                request.DispatchObserver?.Invoke(true);
            }
            response = await pending.ConfigureAwait(false);
        }
        catch (JsonRpcTransportException exception) when (exception.Kind == JsonRpcTransportFailureKind.RequestTimedOut)
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.PromptTimeout,
                $"The ACP session/prompt call did not complete within {_options.TurnHardTimeout}.",
                CursorAcpPolicy.PromptGuidance);
        }
        catch (JsonRpcTransportException exception)
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.TransportFailure,
                $"The ACP stdio transport failed during session/prompt: {exception.Message}",
                CursorAcpPolicy.PromptGuidance);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.PromptTimeout,
                $"The ACP session/prompt call was aborted after {_options.TurnHardTimeout}.",
                CursorAcpPolicy.PromptGuidance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The ACP session/prompt request failed unexpectedly.");

            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.TransportFailure,
                $"The ACP session/prompt request failed: {exception.Message}",
                CursorAcpPolicy.PromptGuidance);
        }

        if (response.Error is { } error)
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.AgentError,
                $"The Cursor ACP agent rejected session/prompt with JSON-RPC error {error.Code}: {error.Message}",
                CursorAcpPolicy.PromptGuidance);
        }

        if (response.Result is not { } resultElement || resultElement.ValueKind != JsonValueKind.Object ||
            !HasUniqueFields(resultElement))
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.MalformedResponse,
                "The ACP session/prompt response did not contain a result object with a stop reason.",
                CursorAcpPolicy.PromptGuidance);
        }

        var stopReason = ReadString(resultElement, "stopReason");

        if (stopReason is null)
        {
            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.MalformedResponse,
                "The ACP session/prompt result contained no non-empty 'stopReason'.",
                CursorAcpPolicy.PromptGuidance);
        }

        _logger.LogInformation(
            "Cursor ACP session/prompt completed with stopReason {StopReason} for session {SessionId}.",
            stopReason,
            request.SessionId);

        return CursorAcpPromptResult.Completed(
            stopReason,
            ReadString(resultElement, "turnId"),
            ReadString(resultElement, "messageId"));
    }

    private async Task<CursorAcpPromptResult?> SelectPromptModeAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken)
    {
        static CursorAcpPromptResult Refuse() => CursorAcpPromptResult.Degraded(
            CursorAcpPromptFailureKind.ModeSelectionFailed,
            "The requested ACP mode was not acknowledged by session/set_mode; no prompt was sent.",
            CursorAcpPolicy.PromptGuidance);

        if (request.ModeId is not ("ask" or "plan" or "agent"))
            return Refuse();

        try
        {
            var response = await _transport.SendRequestAsync(
                SetModeMethod,
                JsonSerializer.SerializeToElement(new { sessionId = request.SessionId, modeId = request.ModeId }),
                _options.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // ACP acknowledges set_mode with an object (the live agent returns {}). If a version
            // includes an explicit mode, contradictory or malformed evidence must fail closed.
            if (response.Error is not null || response.Result is not { ValueKind: JsonValueKind.Object } result)
                return Refuse();
            foreach (var field in new[] { "modeId", "currentModeId" })
            {
                foreach (var property in result.EnumerateObject().Where(p => p.NameEquals(field)))
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != request.ModeId)
                        return Refuse();
            }
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Mode selection never delivered a prompt, even if its own acknowledgement was lost.
            return Refuse();
        }
    }

    private async Task<CursorAcpPromptResult?> SelectPromptModelAsync(CursorAcpPromptRequest request, CancellationToken cancellationToken)
    {
        static CursorAcpPromptResult Refuse() => CursorAcpPromptResult.Degraded(CursorAcpPromptFailureKind.InvalidRequest,
            "The requested model was not acknowledged by session/set_model; no prompt was sent.");
        if (string.IsNullOrWhiteSpace(request.Model)) return Refuse();
        try
        {
            var response = await _transport.SendRequestAsync("session/set_model",
                JsonSerializer.SerializeToElement(new { sessionId = request.SessionId, modelId = request.Model }),
                _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Error is not null || response.Result is not { ValueKind: JsonValueKind.Object } result) return Refuse();
            foreach (var property in result.EnumerateObject().Where(p => p.Name is "modelId" or "currentModelId"))
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != request.Model) return Refuse();
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Refuse();
        }
    }

    public async IAsyncEnumerable<CursorAcpStreamEvent> SubscribeEventsAsync(
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = _transport.Notifications;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var notification))
            {
                var normalized = NormalizeNotification(notification, sessionId);

                if (normalized.Event is { } streamEvent)
                {
                    if (streamEvent is CursorAcpStreamEvent.PermissionRequest permission &&
                        IsCancelledNativePermission(permission))
                    {
                        await ReplyNativePermissionAsync(new CursorAcpPermissionReplyRequest
                        {
                            PermissionId = permission.RequestId, SessionId = permission.SessionId,
                            Decision = CursorAcpPermissionDecision.Deny
                        }, cancelled: true, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    yield return streamEvent;
                    continue;
                }

                if (normalized.IsMalformed)
                {
                    Interlocked.Increment(ref _skippedStreamEventCount);
                    await RejectMalformedPermissionAsync(notification, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentReadiness is not { IsReady: true })
        {
            return CursorAcpPermissionReplyResult.Degraded(
                CursorAcpPermissionReplyFailureKind.NotReady,
                "A permission reply requires a successful initialize handshake first; the current readiness " +
                "is not ready.",
                CursorAcpPolicy.PermissionReplyGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.PermissionId))
        {
            return CursorAcpPermissionReplyResult.Degraded(
                CursorAcpPermissionReplyFailureKind.InvalidPermissionId,
                "A permission reply requires the non-empty 'permissionId' of the request being answered.",
                CursorAcpPolicy.PermissionReplyGuidance);
        }

        if (request.PermissionId.StartsWith(NativePermissionPrefix, StringComparison.Ordinal))
            return await ReplyNativePermissionAsync(request, cancelled: false, cancellationToken).ConfigureAwait(false);

        // Even the legacy wire schema is normalized to a captured opaque receipt. Arbitrary UI
        // strings must never inject a JSON-RPC response or bypass session/one-shot ownership.
        return InvalidNativeReply("No captured pending permission receipt exists for this identifier.");
    }

    public async Task<CursorAcpCancelResult> CancelSessionAsync(
        CursorAcpCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentReadiness is not { IsReady: true })
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.NotReady,
                "The ACP session/cancel call requires a successful initialize handshake first; the current " +
                "readiness is not ready.",
                CursorAcpPolicy.CancelGuidance);
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.InvalidSessionId,
                "The ACP session/cancel call requires a non-empty 'sessionId'.",
                CursorAcpPolicy.CancelGuidance);
        }

        using var sendBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendBudget.CancelAfter(_options.RequestTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            lock (_nativePermissionSync) _cancelledPermissionSessions.Add(request.SessionId);
            await _transport
                .SendNotificationAsync(
                    CancelMethod,
                    BuildCancelParameters(request),
                    sendBudget.Token)
                .ConfigureAwait(false);
            await CancelNativePermissionsAsync(request.SessionId, sendBudget.Token).ConfigureAwait(false);
        }
        catch (JsonRpcTransportException exception) when (exception.Kind == JsonRpcTransportFailureKind.RequestTimedOut)
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.CancelTimeout,
                $"The ACP session/cancel call did not complete within {_options.RequestTimeout}.",
                CursorAcpPolicy.CancelGuidance);
        }
        catch (JsonRpcTransportException exception)
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.TransportFailure,
                $"The ACP stdio transport failed during session/cancel: {exception.Message}",
                CursorAcpPolicy.CancelGuidance);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.CancelTimeout,
                $"The ACP session/cancel call was aborted after {_options.RequestTimeout}.",
                CursorAcpPolicy.CancelGuidance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The ACP session/cancel request failed unexpectedly.");

            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.TransportFailure,
                $"The ACP session/cancel request failed: {exception.Message}",
                CursorAcpPolicy.CancelGuidance);
        }

        _logger.LogInformation(
            "Cursor ACP session/cancel notification was sent for session {SessionId}; awaiting terminal evidence.",
            request.SessionId);

        return CursorAcpCancelResult.Accepted();
    }

    private async Task<CursorAcpSessionResult> SendSessionRequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        JsonRpcResponse response;

        try
        {
            response = await _transport
                .SendRequestAsync(method, parameters, _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcTransportException exception) when (exception.Kind == JsonRpcTransportFailureKind.RequestTimedOut)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.SessionTimeout,
                $"The ACP {method} call did not complete within {_options.RequestTimeout}.",
                CursorAcpPolicy.SessionRequestGuidance);
        }
        catch (JsonRpcTransportException exception)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.TransportFailure,
                $"The ACP stdio transport failed during {method}: {exception.Message}",
                CursorAcpPolicy.SessionRequestGuidance);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.SessionTimeout,
                $"The ACP {method} call was aborted after {_options.RequestTimeout}.",
                CursorAcpPolicy.SessionRequestGuidance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The ACP {Method} request failed unexpectedly.", method);

            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.TransportFailure,
                $"The ACP {method} request failed: {exception.Message}",
                CursorAcpPolicy.SessionRequestGuidance);
        }

        if (response.Error is { } error)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.AgentError,
                $"The Cursor ACP agent rejected {method} with JSON-RPC error {error.Code}: {error.Message}",
                CursorAcpPolicy.SessionRequestGuidance);
        }

        if (response.Result is not { } resultElement || resultElement.ValueKind != JsonValueKind.Object)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.MalformedResponse,
                $"The ACP {method} response did not contain a result object with a session id.",
                CursorAcpPolicy.SessionRequestGuidance);
        }

        var sessionId = ReadSessionId(resultElement);
        if (method == LoadSessionMethod)
        {
            // ACP v1 load acknowledges the requested session with {}, unlike session/new.
            // Only the successful correlated response permits reuse of the requested identifier.
            var requestedId = parameters.GetProperty("sessionId").GetString()!;
            foreach (var property in resultElement.EnumerateObject().Where(p => p.NameEquals("sessionId") || p.NameEquals("id")))
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != requestedId)
                    return CursorAcpSessionResult.Degraded(CursorAcpSessionFailureKind.MalformedResponse,
                        "The ACP session/load response contradicted the requested native session.", CursorAcpPolicy.SessionLoadGuidance);
            }
            sessionId = requestedId;
        }

        if (sessionId is null)
        {
            return CursorAcpSessionResult.Degraded(
                CursorAcpSessionFailureKind.MalformedResponse,
                $"The ACP {method} result contained neither a non-empty 'sessionId' nor a non-empty 'id'.",
                CursorAcpPolicy.SessionRequestGuidance);
        }

        _logger.LogInformation("Cursor ACP {Method} succeeded with native session id {SessionId}.", method, sessionId);

        return CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence
        {
            SessionId = sessionId,
            AvailableModeIds = ReadAvailableModes(resultElement)
        });
    }

    private static IReadOnlyList<string>? ReadAvailableModes(JsonElement result)
    {
        // Discovery is optional. Do not infer it from the current mode, display names or prose.
        // Reject duplicate structural fields instead of silently trusting the last occurrence.
        var modesFields = result.EnumerateObject().Where(p => p.NameEquals("modes")).ToArray();
        if (modesFields.Length != 1 || modesFields[0].Value.ValueKind != JsonValueKind.Object)
            return null;
        var listFields = modesFields[0].Value.EnumerateObject().Where(p => p.NameEquals("availableModes")).ToArray();
        if (listFields.Length != 1 || listFields[0].Value.ValueKind != JsonValueKind.Array)
            return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in listFields[0].Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            var idFields = item.EnumerateObject().Where(p => p.NameEquals("id")).ToArray();
            if (idFields.Length != 1 || idFields[0].Value.ValueKind != JsonValueKind.String)
                return null;
            var id = idFields[0].Value.GetString();
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || !ids.Add(id)) return null;
        }
        return Array.AsReadOnly(ids.ToArray());
    }

    private async Task<CursorAcpHandshakeResult> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        JsonRpcResponse response;

        try
        {
            response = await _transport
                .SendRequestAsync(
                    InitializeMethod,
                    BuildInitializeParameters(),
                    _options.HandshakeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcTransportException exception) when (exception.Kind == JsonRpcTransportFailureKind.RequestTimedOut)
        {
            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.HandshakeTimeout,
                $"The ACP initialize handshake did not complete within {_options.HandshakeTimeout}.",
                CursorAcpPolicy.HandshakeGuidance);
        }
        catch (JsonRpcTransportException exception)
        {
            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.TransportFailure,
                $"The ACP stdio transport failed during initialize: {exception.Message}",
                CursorAcpPolicy.HandshakeGuidance);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.HandshakeTimeout,
                $"The ACP initialize handshake was aborted after {_options.HandshakeTimeout}.",
                CursorAcpPolicy.HandshakeGuidance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The ACP initialize request failed unexpectedly.");

            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.TransportFailure,
                $"The ACP initialize request failed: {exception.Message}",
                CursorAcpPolicy.HandshakeGuidance);
        }

        if (response.Error is { } error)
        {
            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.AgentError,
                $"The Cursor ACP agent rejected initialize with JSON-RPC error {error.Code}: {error.Message}",
                CursorAcpPolicy.HandshakeGuidance);
        }

        if (response.Result is not { } resultElement)
        {
            return CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.MalformedResponse,
                "The ACP initialize response contained neither a result nor an error object.",
                CursorAcpPolicy.HandshakeGuidance);
        }

        try
        {
            var evidence = _validator.ValidateInitializeResult(resultElement);

            _logger.LogInformation(
                "Cursor ACP handshake succeeded with protocolVersion {ProtocolVersion} and {AuthMethodCount} auth method(s).",
                evidence.ProtocolVersion,
                evidence.AuthMethods.Count);

            return CursorAcpHandshakeResult.Ready(evidence);
        }
        catch (CursorAcpProtocolViolationException exception)
        {
            var failureKind = exception.Kind switch
            {
                CursorAcpProtocolViolationKind.UnsupportedVersion => CursorAcpHandshakeFailureKind.UnsupportedVersion,
                CursorAcpProtocolViolationKind.MissingRequiredCapability => CursorAcpHandshakeFailureKind.MissingRequiredCapability,
                _ => CursorAcpHandshakeFailureKind.MalformedResponse
            };

            _logger.LogWarning("Cursor ACP handshake validation failed: {Blocker}", exception.Message);

            return CursorAcpHandshakeResult.Degraded(
                failureKind,
                exception.Message,
                CursorAcpPolicy.UnsupportedVersionGuidance);
        }
    }

    private JsonElement BuildInitializeParameters()
    {
        var payload = new InitializeParametersPayload
        {
            ProtocolVersion = CursorAcpHandshakeValidator.SupportedProtocolVersion,
            ClientInfo = new ClientInfoPayload
            {
                Name = _options.ClientName,
                Version = _options.ClientVersion
            },
            Capabilities = new JsonObject()
        };

        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    private static JsonElement BuildNewSessionParameters(CursorAcpNewSessionRequest request)
    {
        var payload = new NewSessionParametersPayload
        {
            WorkingDirectory = request.WorkingDirectory,
            McpServers = BuildMcpServers(request.McpServers)
        };

        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    private static JsonElement BuildLoadSessionParameters(CursorAcpLoadSessionRequest request)
    {
        var payload = new LoadSessionParametersPayload
        {
            SessionId = request.SessionId,
            WorkingDirectory = request.WorkingDirectory,
            McpServers = BuildMcpServers(request.McpServers)
        };

        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    private static JsonElement BuildPromptParameters(CursorAcpPromptRequest request)
    {
        var payload = new PromptParametersPayload
        {
            SessionId = request.SessionId,
            Prompt = new[]
            {
                new PromptContentBlockPayload
                {
                    Type = "text",
                    Text = request.Prompt
                }
            },
            Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model
        };

        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    private static JsonElement BuildCancelParameters(CursorAcpCancelRequest request)
    {
        var payload = new CancelParametersPayload { SessionId = request.SessionId };

        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    private static McpServerPayload[] BuildMcpServers(IReadOnlyList<CursorAcpMcpServer> mcpServers) =>
        mcpServers
            .Select(server => new McpServerPayload
            {
                Name = server.Name,
                Command = server.Command,
                Args = server.Args,
                Type = server.Type,
                Url = server.Url
            })
            .ToArray();

    private static string? ReadSessionId(JsonElement resultElement)
    {
        // Baseline deserialization until a live session/new response is captured: the native
        // session identifier is accepted from 'sessionId' or the alternative 'id' property. The
        // JSON-RPC envelope id is never used as a session id.
        string? sessionId = null;
        foreach (var property in resultElement.EnumerateObject().Where(p => p.NameEquals("sessionId") || p.NameEquals("id")))
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                return null;
            var candidate = property.Value.GetString()!;
            if (sessionId is not null && !string.Equals(sessionId, candidate, StringComparison.Ordinal))
                return null;
            sessionId = candidate;
        }

        return sessionId;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private NormalizationResult NormalizeNotification(
        JsonRpcNotification notification,
        string? sessionFilter)
    {
        return notification.Method switch
        {
            SessionUpdateMethod => NormalizeSessionUpdate(notification, sessionFilter),
            RequestPermissionMethod => NormalizePermissionRequest(notification, sessionFilter),
            _ => NormalizationResult.Malformed
        };
    }

    private static NormalizationResult NormalizeSessionUpdate(
        JsonRpcNotification notification,
        string? sessionFilter)
    {
        if (notification.Parameters is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
        {
            return NormalizationResult.Malformed;
        }

        var sessionId = ReadString(parameters, "sessionId");

        if (!TryMatchSession(sessionId, sessionFilter, out var isMalformed))
        {
            return isMalformed ? NormalizationResult.Malformed : NormalizationResult.Skipped;
        }

        if (!parameters.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            return NormalizationResult.Malformed;
        }

        var updateType = ReadString(update, "type");
        var nativeUpdateType = ReadString(update, "sessionUpdate");

        // Current ACP uses sessionUpdate plus a typed content block. Retain the older captured
        // dialect, but never let conflicting discriminators reinterpret an event as public text.
        if (update.TryGetProperty("sessionUpdate", out _))
        {
            if (nativeUpdateType is null || update.TryGetProperty("type", out _))
                return NormalizationResult.Malformed;
            if (nativeUpdateType is "agent_message_chunk" or "agent_thought_chunk")
            {
                if (!update.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Object || ReadString(content, "type") != "text" ||
                    ReadString(content, "text") is not { } contentText)
                    return NormalizationResult.Malformed;
                return nativeUpdateType == "agent_message_chunk"
                    ? NormalizationResult.Emitted(new CursorAcpStreamEvent.TextChunk { SessionId = sessionId, Method = notification.Method, Text = contentText })
                    : NormalizationResult.Emitted(new CursorAcpStreamEvent.Thought { SessionId = sessionId, Method = notification.Method, Text = contentText });
            }
            // Catalog and mode announcements are metadata, not prompt delivery or cancellation.
            if (nativeUpdateType is "current_mode_update" or "session_info_update" or "available_commands_update" or "config_option_update")
                return NormalizationResult.Skipped;
            return NormalizationResult.Malformed;
        }

        switch (updateType)
        {
            case "text":
            {
                var text = ReadString(update, "text");

                return text is null
                    ? NormalizationResult.Malformed
                    : NormalizationResult.Emitted(new CursorAcpStreamEvent.TextChunk
                    {
                        SessionId = sessionId,
                        Method = notification.Method,
                        Text = text
                    });
            }

            case "thought":
            {
                var text = ReadString(update, "text");

                return text is null
                    ? NormalizationResult.Malformed
                    : NormalizationResult.Emitted(new CursorAcpStreamEvent.Thought
                    {
                        SessionId = sessionId,
                        Method = notification.Method,
                        Text = text
                    });
            }

            case "tool_call":
            {
                var callId = ReadString(update, "callId") ?? ReadString(update, "id");
                var toolName = ReadString(update, "name") ?? ReadString(update, "toolName");

                if (callId is null || toolName is null)
                {
                    return NormalizationResult.Malformed;
                }

                return NormalizationResult.Emitted(new CursorAcpStreamEvent.ToolCall
                {
                    SessionId = sessionId,
                    Method = notification.Method,
                    CallId = callId,
                    ToolName = toolName,
                    Arguments = update.TryGetProperty("arguments", out var arguments)
                        ? arguments.Clone()
                        : null
                });
            }

            case "status":
            {
                var phase = ReadString(update, "phase") ?? ReadString(update, "state");

                return phase is null
                    ? NormalizationResult.Malformed
                    : NormalizationResult.Emitted(new CursorAcpStreamEvent.StatusUpdate
                    {
                        SessionId = sessionId,
                        Method = notification.Method,
                        Phase = phase
                    });
            }

            default:
                return NormalizationResult.Malformed;
        }
    }

    private NormalizationResult NormalizePermissionRequest(
        JsonRpcNotification notification,
        string? sessionFilter)
    {
        if (notification.Parameters is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
        {
            return NormalizationResult.Malformed;
        }

        var sessionId = ReadString(parameters, "sessionId");

        if (!TryMatchSession(sessionId, sessionFilter, out var isMalformed))
        {
            return isMalformed ? NormalizationResult.Malformed : NormalizationResult.Skipped;
        }

        if (parameters.TryGetProperty("toolCall", out _) || parameters.TryGetProperty("options", out _))
            return NormalizeNativePermission(notification, parameters, sessionId);

        var description = ReadString(parameters, "description") ?? ReadString(parameters, "title");

        if (description is null || sessionId is null || notification.Id is not { } rpcId
            || rpcId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
            || (rpcId.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(rpcId.GetString())))
        {
            return NormalizationResult.Malformed;
        }

        var requestId = NativePermissionPrefix + Guid.NewGuid().ToString("N");
        lock (_nativePermissionSync)
        {
            if (!CanCapturePermissionId(rpcId))
                return NormalizationResult.Malformed;
            _nativePermissions.Add(requestId, new NativePermission(rpcId.Clone(), sessionId, "allow_once", "deny", IsLegacy: true));
        }

        return NormalizationResult.Emitted(new CursorAcpStreamEvent.PermissionRequest
        {
            SessionId = sessionId,
            Method = notification.Method,
            RequestId = requestId,
            Description = description,
            CanAllowOnce = true, // Captured legacy one-shot schema, not a DTO default.
            RawPayload = parameters.Clone()
        });
    }

    private static bool TryMatchSession(string? sessionId, string? sessionFilter, out bool isMalformed)
    {
        isMalformed = false;

        if (sessionFilter is null)
        {
            return true;
        }

        if (sessionId is null)
        {
            isMalformed = true;
            return false;
        }

        return string.Equals(sessionId, sessionFilter, StringComparison.Ordinal);
    }

    private static string? ReadIdAsString(JsonElement? id)
    {
        if (id is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private readonly record struct NormalizationResult(CursorAcpStreamEvent? Event, bool IsMalformed)
    {
        public static NormalizationResult Emitted(CursorAcpStreamEvent streamEvent) => new(streamEvent, false);

        public static NormalizationResult Malformed => new(null, true);

        public static NormalizationResult Skipped => new(null, false);
    }

    private sealed class InitializeParametersPayload
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; init; }

        [JsonPropertyName("clientInfo")]
        public required ClientInfoPayload ClientInfo { get; init; }

        [JsonPropertyName("capabilities")]
        public required JsonObject Capabilities { get; init; }
    }

    private sealed class ClientInfoPayload
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("version")]
        public required string Version { get; init; }
    }

    private sealed class NewSessionParametersPayload
    {
        [JsonPropertyName("cwd")]
        public required string WorkingDirectory { get; init; }

        [JsonPropertyName("mcpServers")]
        public required IReadOnlyList<McpServerPayload> McpServers { get; init; }
    }

    private sealed class LoadSessionParametersPayload
    {
        [JsonPropertyName("sessionId")]
        public required string SessionId { get; init; }

        [JsonPropertyName("cwd")]
        public required string WorkingDirectory { get; init; }

        [JsonPropertyName("mcpServers")]
        public required IReadOnlyList<McpServerPayload> McpServers { get; init; }
    }

    private sealed class PromptParametersPayload
    {
        [JsonPropertyName("sessionId")]
        public required string SessionId { get; init; }

        [JsonPropertyName("prompt")]
        public required IReadOnlyList<PromptContentBlockPayload> Prompt { get; init; }

        [JsonPropertyName("model")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Model { get; init; }
    }

    private sealed class PromptContentBlockPayload
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private sealed class CancelParametersPayload
    {
        [JsonPropertyName("sessionId")]
        public required string SessionId { get; init; }
    }

    private sealed class PermissionReplyPayload
    {
        [JsonPropertyName("decision")]
        public required string Decision { get; init; }
    }

    private sealed class McpServerPayload
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("command")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Command { get; init; }

        [JsonPropertyName("args")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Args { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; init; }
    }
}
