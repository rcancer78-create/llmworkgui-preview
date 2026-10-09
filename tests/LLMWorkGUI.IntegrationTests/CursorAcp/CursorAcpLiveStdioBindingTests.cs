using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

/// <summary>
/// Proves the end-to-end Phase 6 binding: the managed ACP process is launched through the shared
/// process supervisor, its live stdio duplex channel is owned by the JSON-RPC transport, and
/// <see cref="CursorAcpClient"/> completes the real <c>initialize</c> handshake over that channel
/// (ADR-0003 §1, §2, ТЗ §4.2, §6.8).
/// </summary>
[Collection(CursorAcpProcessCollection.Name)]
public sealed class CursorAcpLiveStdioBindingTests
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task StartAsync_LiveProcess_BindsTransportAndCompletesRealHandshake()
    {
        var handshakeResult = CursorAcpIntegrationTestData.ReadHandshakeResult();

        using var agent = new FakeAcpAgentExecutable(handshakeResult.GetRawText());
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(agent.ExecutablePath, dataDirectory.Root);

        var startResult = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-live-handshake"
        });

        Assert.True(startResult.IsStarted);

        var session = startResult.Session!;

        try
        {
            // The transport is bound to the live process stdio, not to a loopback pipe pair.
            Assert.NotNull(session.Transport);

            var client = new CursorAcpClient(
                session.Transport!,
                new CursorAcpOptions { HandshakeTimeout = HandshakeTimeout });

            var handshake = await client.InitializeAsync();

            Assert.True(handshake.IsReady, handshake.Blocker ?? "The live ACP handshake was not ready.");
            Assert.Equal(1, handshake.Evidence!.ProtocolVersion);
            Assert.True(handshake.Evidence.AgentCapabilities.LoadSession);
            Assert.Same(handshake, client.CurrentReadiness);
        }
        finally
        {
            await manager.StopAsync(session);
        }

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StopAsync_LiveProcess_ClosesTransportBeforeTerminatingTree()
    {
        var handshakeResult = CursorAcpIntegrationTestData.ReadHandshakeResult();

        using var agent = new FakeAcpAgentExecutable(handshakeResult.GetRawText());
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(agent.ExecutablePath, dataDirectory.Root);

        var startResult = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-live-stop"
        });

        Assert.True(startResult.IsStarted);

        var session = startResult.Session!;
        var transport = session.Transport!;

        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions { HandshakeTimeout = HandshakeTimeout });

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady, handshake.Blocker ?? "The live ACP handshake was not ready.");

        await manager.StopAsync(session);

        Assert.False(session.IsRunning);

        // After the session stopped, the closed transport must fail fast with a typed transport
        // failure instead of waiting for the request timeout.
        var afterStop = await client.CreateSessionAsync(new CursorAcpNewSessionRequest
        {
            WorkingDirectory = dataDirectory.Root
        });

        Assert.True(afterStop.IsDegraded);
        Assert.Null(afterStop.Evidence);
    }

    [Fact]
    public async Task StartAsync_LiveProcess_DoesNotSpoolProtocolStandardOutput()
    {
        var handshakeResult = CursorAcpIntegrationTestData.ReadHandshakeResult();

        using var agent = new FakeAcpAgentExecutable(handshakeResult.GetRawText());
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(agent.ExecutablePath, dataDirectory.Root);

        var startResult = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-live-no-stdout-spool"
        });

        Assert.True(startResult.IsStarted);

        var session = startResult.Session!;

        var client = new CursorAcpClient(
            session.Transport!,
            new CursorAcpOptions { HandshakeTimeout = HandshakeTimeout });

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady, handshake.Blocker ?? "The live ACP handshake was not ready.");

        await manager.StopAsync(session);

        var standardOutputSpool = Path.Combine(
            session.RunDirectory,
            ProcessSupervisorOptions.StandardOutputFileName);

        Assert.False(
            File.Exists(standardOutputSpool),
            "Protocol frames must never be spooled to disk: standard output belongs to the transport.");

        Assert.True(File.Exists(Path.Combine(
            session.RunDirectory,
            ProcessSupervisorOptions.StandardErrorFileName)));
    }

    private static CursorAcpProcessManager CreateManager(string executablePath, string appDataDirectory)
    {
        var options = Options.Create(new CursorAcpOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(15),
            HandshakeTimeout = HandshakeTimeout,
            RequestTimeout = TimeSpan.FromSeconds(30)
        });

        var supervisor = new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions
            {
                GracefulShutdownTimeout = TimeSpan.FromSeconds(2)
            }),
            new StorageOptions { AppDataDirectory = appDataDirectory });

        return new CursorAcpProcessManager(
            StubCursorExecutableResolver.Found(executablePath),
            supervisor,
            new StorageOptions { AppDataDirectory = appDataDirectory },
            options,
            new JsonRpcStdioTransportFactory(options));
    }
}
