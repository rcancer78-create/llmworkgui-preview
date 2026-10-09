using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

/// <summary>Managed ACP process session double; it owns no OS process.</summary>
internal sealed class StubCursorAcpProcessSession : ICursorAcpProcessSession
{
    public string ExecutionId => "ui-test-execution";

    public int? ProcessId => 4242;

    public long ProcessGeneration { get; set; } = 42;

    public string WorkingDirectory => Path.Combine(Path.GetTempPath(), "llmworkgui-ui-cursor");

    public string RunDirectory => Path.Combine(Path.GetTempPath(), "llmworkgui-ui-cursor-run");

    public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;

    public IJsonRpcTransport? Transport => null;

    public bool IsRunning { get; private set; } = true;

    public ValueTask DisposeAsync()
    {
        IsRunning = false;

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Protocol client double for UI tests. The UI never calls the protocol directly, so every member
/// fails loudly instead of silently pretending to speak ACP.
/// </summary>
internal sealed class StubCursorAcpClient : ICursorAcpClient
{
    public CursorAcpHandshakeResult? CurrentReadiness => null;

    public long SkippedStreamEventCount => 0;

    public Task<CursorAcpHandshakeResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI test client double does not perform an ACP handshake.");

    public Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI drives sessions through the lifecycle service.");

    public Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI drives sessions through the lifecycle service.");

    public Task<CursorAcpPromptResult> PromptAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI drives turns through the lifecycle service.");

    public IAsyncEnumerable<CursorAcpStreamEvent> SubscribeEventsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI observes events through the lifecycle service.");

    public Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI replies through the lifecycle service.");

    public Task<CursorAcpCancelResult> CancelSessionAsync(
        CursorAcpCancelRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The UI cancels through the lifecycle service.");
}
