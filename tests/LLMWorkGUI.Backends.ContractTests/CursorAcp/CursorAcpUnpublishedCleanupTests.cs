using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpUnpublishedCleanupTests
{
    [Fact]
    public async Task FailedStartAndFailedCleanupRetainProcessForExplicitStopRetry()
    {
        var session = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(session);
        manager.StopHandler = (_, _) => Task.FromException(new IOException("synthetic cleanup failure"));
        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromException<CursorAcpHandshakeResult>(new InvalidOperationException("synthetic handshake failure"))
        };
        await using var service = CursorAcpSessionLifecycleStartTests.CreateService(manager, client);
        var result = await service.StartBackendAsync("unpublished-cleanup");
        Assert.Equal(CursorAcpBackendFailureKind.ProcessCleanupPending, result.FailureKind);
        Assert.True(result.RequiresReconciliation);
        Assert.True(session.IsRunning);

        manager.StopHandler = null;
        await service.StopBackendAsync();

        Assert.False(session.IsRunning);
        Assert.Equal(2, manager.StoppedSessions.Count);
        Assert.All(manager.StoppedSessions, stopped => Assert.Same(session, stopped));
    }
}
