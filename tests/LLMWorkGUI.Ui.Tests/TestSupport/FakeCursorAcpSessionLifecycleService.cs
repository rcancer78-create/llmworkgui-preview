using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

/// <summary>
/// Deterministic Cursor ACP lifecycle double for UI tests. It never starts a process and never
/// spends a model quota; every outcome is scripted by the test.
/// </summary>
internal sealed class FakeCursorAcpSessionLifecycleService : ICursorAcpSessionLifecycleService
{
    private readonly List<string> _ancestry = new();

    public CursorAcpBackendStartResult? Current { get; private set; }

    public string? NativeSessionId { get; private set; }

    public IReadOnlyList<string> Ancestry => _ancestry;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public Exception? StartFailure { get; set; }

    public Exception? StopFailure { get; set; }

    public List<CursorAcpTurnRequest> TurnRequests { get; } = new();

    public List<CursorAcpNewSessionRequest> SessionRequests { get; } = new();

    public List<CursorAcpPermissionReplyRequest> PermissionReplies { get; } = new();

    public int CancelCount { get; private set; }

    public Func<CursorAcpBackendStartResult>? StartHandler { get; set; }

    public Func<CursorAcpSessionResult>? SessionHandler { get; set; }

    public Func<CursorAcpTurnRequest, CursorAcpTurnResult>? TurnHandler { get; set; }

    public Func<CursorAcpCancelResult>? CancelHandler { get; set; }

    public event EventHandler<CursorAcpStreamEvent>? StreamEventObserved;

    public event EventHandler<CursorAcpStreamEvent.PermissionRequest>? PermissionRequested;

    public event EventHandler<CursorAcpTurnStateChangedEventArgs>? TurnStateChanged;

    public Task<CursorAcpBackendStartResult> StartBackendAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (StartFailure is not null)
        {
            return Task.FromException<CursorAcpBackendStartResult>(StartFailure);
        }

        var result = StartHandler?.Invoke() ?? CursorAcpBackendStartResult.Degraded(
            CursorAcpBackendFailureKind.ExecutableUnavailable,
            "Cursor Agent is not installed in this test environment.");

        Current = result.IsReady ? result : null;

        return Task.FromResult(result);
    }

    public Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        SessionRequests.Add(request);
        return Task.FromResult(TrackSession(SessionHandler?.Invoke() ?? NotReadyResult()));
    }

    public Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TrackSession(SessionHandler?.Invoke() ?? NotReadyResult()));

    public Task<CursorAcpSessionResult> ResetSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default) => CreateSessionAsync(request, cancellationToken);

    public Task<CursorAcpTurnResult> ExecuteTurnAsync(
        CursorAcpTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        TurnRequests.Add(request);

        var result = TurnHandler?.Invoke(request) ?? new CursorAcpTurnResult
        {
            Outcome = CursorAcpTurnOutcome.Succeeded,
            SessionId = request.Prompt.SessionId,
            ClientRequestId = request.Prompt.ClientRequestId,
            StopReason = CursorAcpStopReasons.EndTurn
        };

        return Task.FromResult(result);
    }

    public Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default)
    {
        PermissionReplies.Add(request);

        return Task.FromResult(CursorAcpPermissionReplyResult.Sent());
    }

    public Task<CursorAcpCancelResult> CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        CancelCount++;

        return Task.FromResult(CancelHandler?.Invoke() ?? CursorAcpCancelResult.Accepted());
    }

    public Task StopBackendAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        if (StopFailure is not null)
        {
            return Task.FromException(StopFailure);
        }

        Current = null;
        NativeSessionId = null;

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Publishes a normalized stream event as the real service would.</summary>
    public void RaiseStreamEvent(CursorAcpStreamEvent streamEvent) =>
        StreamEventObserved?.Invoke(this, streamEvent);

    /// <summary>Publishes a permission request that must await an explicit user decision.</summary>
    public void RaisePermissionRequest(CursorAcpStreamEvent.PermissionRequest request)
    {
        StreamEventObserved?.Invoke(this, request);
        PermissionRequested?.Invoke(this, request);
    }

    public void RaiseTurnState(string sessionId, CursorAcpTurnState state) =>
        TurnStateChanged?.Invoke(this, new CursorAcpTurnStateChangedEventArgs(sessionId, state));

    public static CursorAcpBackendStartResult CreateReadyStart()
    {
        var session = new StubCursorAcpProcessSession();
        var client = new StubCursorAcpClient();
        var evidence = new CursorAcpHandshakeEvidence
        {
            ProtocolVersion = 1,
            AgentCapabilities = new CursorAcpAgentCapabilities
            {
                LoadSession = true,
                PromptImage = true,
                PromptAudio = false,
                PromptEmbeddedContext = false,
                McpHttp = true,
                McpSse = true,
                SessionList = true
            },
            AuthMethods = Array.Empty<CursorAcpAuthMethod>()
        };

        return CursorAcpBackendStartResult.Ready(session, client, evidence);
    }

    private CursorAcpSessionResult TrackSession(CursorAcpSessionResult result)
    {
        if (result.IsReady && result.Evidence is not null)
        {
            NativeSessionId = result.Evidence.SessionId;

            if (_ancestry.Count == 0 ||
                !string.Equals(_ancestry[^1], result.Evidence.SessionId, StringComparison.Ordinal))
            {
                _ancestry.Add(result.Evidence.SessionId);
            }
        }

        return result;
    }

    private static CursorAcpSessionResult NotReadyResult() =>
        CursorAcpSessionResult.Degraded(
            CursorAcpSessionFailureKind.NotReady,
            "The Cursor ACP backend is not started in this test.");
}
