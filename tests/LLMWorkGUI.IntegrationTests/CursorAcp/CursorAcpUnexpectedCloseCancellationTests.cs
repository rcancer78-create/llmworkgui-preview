using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

[Collection(CursorAcpProcessCollection.Name)]
public sealed class CursorAcpUnexpectedCloseCancellationTests
{
    [Fact]
    public async Task UnexpectedTransportCancellation_DoesNotSkipOwnedPhysicalStop()
    {
        using var executable = new FakeCursorAgentExecutable();
        using var directory = new TestDirectory();
        var options = Options.Create(new CursorAcpOptions { ShutdownTimeout = TimeSpan.FromSeconds(15) });
        var factory = new UnexpectedCancellationFactory(options.Value);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
            { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) }),
            new StorageOptions { AppDataDirectory = directory.Root });
        var manager = new CursorAcpProcessManager(StubCursorExecutableResolver.Found(executable.ExecutablePath),
            supervisor, new StorageOptions { AppDataDirectory = directory.Root }, options, factory);
        var started = await manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = "unexpected-close-cancellation" });
        Assert.True(started.IsStarted, started.Blocker);
        var session = started.Session!;
        try
        {
            var childId = await FakeCursorAgentExecutable.WaitForPidAsync(executable.ChildProcessIdFilePath,
                TimeSpan.FromSeconds(60));
            Assert.True(FakeCursorAgentExecutable.IsProcessAlive(childId));

            var failure = await Record.ExceptionAsync(() => manager.StopAsync(session));

            Assert.False(factory.Transport!.ReceivedCancelledToken);
            Assert.False(session.IsRunning);
            Assert.True(await FakeCursorAgentExecutable.WaitForProcessExitAsync(childId, TimeSpan.FromSeconds(30)));
            Assert.Null(failure);
        }
        finally
        {
            // The RED path must not leak its real tree or depend on a process-wide test cleanup.
            factory.Transport!.ThrowOnClose = false;
            await manager.StopAsync(session);
            await session.DisposeAsync();
        }
    }

    private sealed class UnexpectedCancellationFactory(CursorAcpOptions options) : IJsonRpcTransportFactory
    {
        public UnexpectedCancellationTransport? Transport { get; private set; }
        public IJsonRpcTransport Create(Stream agentStandardOutput, Stream agentStandardInput) =>
            Transport = new UnexpectedCancellationTransport(new JsonRpcStdioTransport(agentStandardOutput, agentStandardInput, options));
    }

    private sealed class UnexpectedCancellationTransport(IJsonRpcTransport inner) : IJsonRpcTransport
    {
        public bool ThrowOnClose { get; set; } = true;
        public bool ReceivedCancelledToken { get; private set; }
        public ChannelReader<JsonRpcNotification> Notifications => inner.Notifications;
        public int MalformedFrameCount => inner.MalformedFrameCount;
        public Task<JsonRpcResponse> SendRequestAsync(string method, JsonElement? parameters = null,
            TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            inner.SendRequestAsync(method, parameters, timeout, cancellationToken);
        public Task SendNotificationAsync(string method, JsonElement? parameters = null,
            CancellationToken cancellationToken = default) => inner.SendNotificationAsync(method, parameters, cancellationToken);
        public Task SendResponseAsync(JsonElement id, JsonElement? result = null, JsonRpcError? error = null,
            CancellationToken cancellationToken = default) => inner.SendResponseAsync(id, result, error, cancellationToken);
        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            ReceivedCancelledToken = cancellationToken.IsCancellationRequested;
            return ThrowOnClose ? Task.FromException(new OperationCanceledException("Synthetic independent close cancellation."))
                : inner.CloseAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
