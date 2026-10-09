using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// Fake process manager for lifecycle tests. It returns a scripted start result and records the
/// stop calls so tests can prove that a failed handshake or an unbound transport still stops the
/// managed process.
/// </summary>
internal sealed class FakeCursorAcpProcessManager : ICursorAcpProcessManager
{
    private readonly Func<CursorAcpProcessStartRequest, CursorAcpProcessStartResult> _startHandler;

    public FakeCursorAcpProcessManager(
        Func<CursorAcpProcessStartRequest, CursorAcpProcessStartResult> startHandler)
    {
        ArgumentNullException.ThrowIfNull(startHandler);

        _startHandler = startHandler;
    }

    public List<string> StartedExecutionIds { get; } = new();

    public List<ICursorAcpProcessSession> StoppedSessions { get; } = new();

    public Func<ICursorAcpProcessSession, CancellationToken, Task>? StopHandler { get; set; }

    public Task<CursorAcpProcessStartResult> StartAsync(
        CursorAcpProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        StartedExecutionIds.Add(request.ExecutionId);

        return Task.FromResult(_startHandler(request));
    }

    public Task StopAsync(ICursorAcpProcessSession session, CancellationToken cancellationToken = default)
    {
        StoppedSessions.Add(session);

        if (StopHandler is not null) return StopHandler(session, cancellationToken);

        return session is FakeCursorAcpProcessSession tracked
            ? tracked.StopAsync()
            : Task.CompletedTask;
    }

    public static FakeCursorAcpProcessManager Started(FakeCursorAcpProcessSession session) =>
        new(_ => CursorAcpProcessStartResult.Started(session));

    public static FakeCursorAcpProcessManager Degraded(
        CursorAcpProcessStartFailureKind failureKind,
        string blocker) =>
        new(_ => CursorAcpProcessStartResult.Degraded(
            failureKind,
            blocker,
            CursorAcpPolicy.ProcessStartupGuidance));
}

/// <summary>Fake managed ACP process session exposing a scripted transport.</summary>
internal sealed class FakeCursorAcpProcessSession : ICursorAcpProcessSession
{
    public FakeCursorAcpProcessSession(IJsonRpcTransport? transport, string executionId = "exec-lifecycle")
    {
        Transport = transport;
        ExecutionId = executionId;
    }

    public string ExecutionId { get; }

    public int? ProcessId => 4242;

    public string WorkingDirectory => Path.Combine(Path.GetTempPath(), "cursor-acp-lifecycle");

    public string RunDirectory => Path.Combine(Path.GetTempPath(), "cursor-acp-lifecycle-run");

    public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;

    public IJsonRpcTransport? Transport { get; }

    public bool IsRunning { get; private set; } = true;

    public int StopCount { get; private set; }

    public Task StopAsync()
    {
        StopCount++;
        IsRunning = false;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
