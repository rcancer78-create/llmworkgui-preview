﻿using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// Backend start contract of <see cref="CursorAcpSessionLifecycleService"/>: process, transport and
/// handshake must all succeed, and every failure stops the managed process and degrades honestly
/// without a hidden CLI print-mode fallback (ADR-0003 §1, §2, §7.2).
/// </summary>
public sealed class CursorAcpSessionLifecycleStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpublishedCleanupFailureReturnsPendingAndRetainsSession(bool handshakeThrows)
    {
        var session = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(session);
        manager.StopHandler = (_, _) => Task.FromException(new TimeoutException("cleanup pending"));
        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => handshakeThrows
                ? Task.FromException<CursorAcpHandshakeResult>(new IOException("handshake failed"))
                : Task.FromResult(CursorAcpHandshakeResult.Degraded(CursorAcpHandshakeFailureKind.UnsupportedVersion, "unsupported"))
        };
        await using var service = CreateService(manager, client);
        try
        {
            var result = await service.StartBackendAsync("unpublished-pending");
            Assert.Equal(CursorAcpBackendFailureKind.ProcessCleanupPending, result.FailureKind);
            Assert.True(result.RequiresReconciliation);
            Assert.True(session.IsRunning);
            var repeated = await service.StartBackendAsync("another-id");
            Assert.Equal(CursorAcpBackendFailureKind.ProcessCleanupPending, repeated.FailureKind);
            Assert.Single(manager.StartedExecutionIds);
        }
        finally { manager.StopHandler = null; await service.StopBackendAsync(); }
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task DisposeAsync_UnconfirmedStopCanBeRetried()
    {
        var processSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(processSession);
        var service = CreateService(manager, CreateReadyClient());
        await service.StartBackendAsync("dispose-retry");
        manager.StopHandler = (_, _) => Task.FromException(new IOException("synthetic stop failure"));
        try
        {
            await service.DisposeAsync();
            Assert.NotNull(service.Current);
            Assert.True(processSession.IsRunning);
            manager.StopHandler = null;
            await service.DisposeAsync();
            Assert.Null(service.Current);
            Assert.False(processSession.IsRunning);
            Assert.Equal(2, manager.StoppedSessions.Count);
        }
        finally
        {
            manager.StopHandler = null;
            await service.StopBackendAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopBackendAsync_UnconfirmedStopRetainsHandleForRetry(bool cancelled)
    {
        var processSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(processSession);
        await using var service = CreateService(manager, CreateReadyClient());
        var started = await service.StartBackendAsync("stop-retry");
        manager.StopHandler = (_, _) => cancelled
            ? Task.FromException(new OperationCanceledException("synthetic stop cancellation"))
            : Task.FromException(new IOException("synthetic stop failure"));

        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StopBackendAsync());
        else await Assert.ThrowsAsync<IOException>(() => service.StopBackendAsync());

        Assert.Same(started, service.Current);
        Assert.True(processSession.IsRunning);
        manager.StopHandler = null;
        await service.StopBackendAsync();
        Assert.Null(service.Current);
        Assert.False(processSession.IsRunning);
        Assert.Equal(2, manager.StoppedSessions.Count);
        Assert.All(manager.StoppedSessions, session => Assert.Same(processSession, session));
    }

    [Fact]
    public async Task StartBackendAsync_ReadyHandshake_ExposesSessionClientAndEvidence()
    {
        var transport = FakeJsonRpcTransport.Unused();
        var processSession = new FakeCursorAcpProcessSession(transport);
        var client = CreateReadyClient();
        var processManager = FakeCursorAcpProcessManager.Started(processSession);

        await using var service = CreateService(processManager, client);

        var result = await service.StartBackendAsync("exec-start-ready");

        Assert.True(result.IsReady, result.Blocker);
        Assert.Same(processSession, result.Session);
        Assert.Same(client, result.Client);
        Assert.Equal(1, result.Handshake!.ProtocolVersion);
        Assert.Same(result, service.Current);
        Assert.Equal(new[] { "exec-start-ready" }, processManager.StartedExecutionIds);
        Assert.Empty(processManager.StoppedSessions);
    }

    [Fact]
    public async Task StartBackendAsync_Twice_IsRejectedAsAlreadyStarted()
    {
        var processSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var processManager = FakeCursorAcpProcessManager.Started(processSession);

        await using var service = CreateService(processManager, CreateReadyClient());

        var first = await service.StartBackendAsync("exec-start-once");
        var second = await service.StartBackendAsync("exec-start-twice");

        Assert.True(first.IsReady);
        Assert.False(second.IsReady);
        Assert.Equal(CursorAcpBackendFailureKind.AlreadyStarted, second.FailureKind);

        // The second attempt must never reach the process manager.
        Assert.Single(processManager.StartedExecutionIds);
    }

    [Theory]
    [InlineData(CursorAcpProcessStartFailureKind.ExecutableMissing, CursorAcpBackendFailureKind.ExecutableUnavailable)]
    [InlineData(CursorAcpProcessStartFailureKind.ExecutableUnresolved, CursorAcpBackendFailureKind.ExecutableUnavailable)]
    [InlineData(CursorAcpProcessStartFailureKind.LaunchFailed, CursorAcpBackendFailureKind.ProcessLaunchFailed)]
    [InlineData(CursorAcpProcessStartFailureKind.ProcessCleanupPending, CursorAcpBackendFailureKind.ProcessCleanupPending)]
    public async Task StartBackendAsync_DegradedProcessStart_MapsFailureKind(
        CursorAcpProcessStartFailureKind processFailure,
        CursorAcpBackendFailureKind expected)
    {
        var processManager = FakeCursorAcpProcessManager.Degraded(processFailure, "cursor-agent is unavailable.");

        await using var service = CreateService(processManager, CreateReadyClient());

        var result = await service.StartBackendAsync("exec-start-degraded");

        Assert.False(result.IsReady);
        Assert.Equal(expected, result.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Blocker));
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task StartBackendAsync_WithoutBoundTransport_StopsProcessAndReportsTransportUnavailable()
    {
        var processSession = new FakeCursorAcpProcessSession(transport: null);
        var processManager = FakeCursorAcpProcessManager.Started(processSession);

        await using var service = CreateService(processManager, CreateReadyClient());

        var result = await service.StartBackendAsync("exec-start-no-transport");

        Assert.False(result.IsReady);
        Assert.Equal(CursorAcpBackendFailureKind.TransportUnavailable, result.FailureKind);

        // The blocker must state explicitly that no CLI print-mode fallback is attempted.
        Assert.Contains("not attempted", result.Blocker!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(processSession, processManager.StoppedSessions);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task StartBackendAsync_DegradedHandshake_StopsProcessAndReportsHandshakeFailed()
    {
        var processSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var processManager = FakeCursorAcpProcessManager.Started(processSession);

        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromResult(CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.UnsupportedVersion,
                "The agent reported protocolVersion 2 instead of the required JSON number 1.",
                "Update Cursor Agent."))
        };

        await using var service = CreateService(processManager, client);

        var result = await service.StartBackendAsync("exec-start-handshake-failed");

        Assert.False(result.IsReady);
        Assert.Equal(CursorAcpBackendFailureKind.HandshakeFailed, result.FailureKind);
        Assert.Contains("protocolVersion 2", result.Blocker!, StringComparison.Ordinal);
        Assert.Contains(processSession, processManager.StoppedSessions);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task StopBackendAsync_StopsProcessAndClearsState()
    {
        var processSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var processManager = FakeCursorAcpProcessManager.Started(processSession);

        await using var service = CreateService(processManager, CreateReadyClient());

        await service.StartBackendAsync("exec-stop");
        await service.StopBackendAsync();

        Assert.Null(service.Current);
        Assert.Null(service.NativeSessionId);
        Assert.Contains(processSession, processManager.StoppedSessions);

        // Stopping twice is safe and does not stop a foreign session.
        await service.StopBackendAsync();
        Assert.Single(processManager.StoppedSessions);
    }

    [Fact]
    public async Task StartBackendAsync_AfterStop_StartsAgain()
    {
        var firstSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused(), "exec-restart-1");
        var secondSession = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused(), "exec-restart-2");
        var sessions = new Queue<ICursorAcpProcessSession>(new[] { firstSession, (ICursorAcpProcessSession)secondSession });

        var processManager = new FakeCursorAcpProcessManager(
            _ => CursorAcpProcessStartResult.Started(sessions.Dequeue()));

        await using var service = CreateService(processManager, CreateReadyClient());

        var first = await service.StartBackendAsync("exec-restart-1");
        await service.StopBackendAsync();
        var second = await service.StartBackendAsync("exec-restart-2");

        Assert.True(first.IsReady);
        Assert.True(second.IsReady);
        Assert.Same(secondSession, second.Session);
        Assert.Equal(2, processManager.StartedExecutionIds.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartBackendAsync_HandshakeThrows_StopsUnpublishedProcess(bool cancelled)
    {
        var session = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(session);
        Exception failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("handshake failed");
        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromException<CursorAcpHandshakeResult>(failure)
        };
        await using var service = CreateService(manager, client);

        var observed = await Record.ExceptionAsync(() => service.StartBackendAsync("unpublished"));

        Assert.Same(failure, observed);
        Assert.Null(service.Current);
        Assert.Equal(1, session.StopCount);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task DisposeAsync_DuringHandshake_WaitsForOwnedProcessAndStopsItExactlyOnce()
    {
        var session = new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused());
        var manager = FakeCursorAcpProcessManager.Started(session);
        var client = CreateReadyClient();
        var initialize = client.InitializeHandler!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.InitializeHandler = async token =>
        {
            entered.TrySetResult();
            await release.Task;
            return await initialize(token);
        };
        var service = CreateService(manager, client);
        var starting = service.StartBackendAsync("dispose-during-start");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = service.DisposeAsync().AsTask();
        release.TrySetResult();
        try
        {
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(service.Current);
            Assert.Equal(1, session.StopCount);
            Assert.False(session.IsRunning);
        }
        finally
        {
            client.CompleteEvents();
            await service.DisposeAsync();
        }
    }

    internal static FakeCursorAcpClient CreateReadyClient()
    {
        // The evidence is derived from the sanitized handshake fixture through the production
        // validator, so the test never invents capability values.
        var evidence = new CursorAcpHandshakeValidator()
            .ValidateInitializeResult(CursorAcpTestData.ReadHandshakeResult());

        Assert.Equal(1, evidence.ProtocolVersion);

        return new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromResult(CursorAcpHandshakeResult.Ready(evidence))
        };
    }

    internal static CursorAcpSessionLifecycleService CreateService(
        ICursorAcpProcessManager processManager,
        ICursorAcpClient client) =>
        new(
            processManager,
            new StubCursorAcpClientFactory(client),
            new CursorAcpModePolicy());
}

/// <summary>Client factory returning a pre-built fake client for the bound transport.</summary>
internal sealed class StubCursorAcpClientFactory : ICursorAcpClientFactory
{
    private readonly ICursorAcpClient _client;

    public StubCursorAcpClientFactory(ICursorAcpClient client)
    {
        _client = client;
    }

    public IJsonRpcTransport? LastTransport { get; private set; }

    public ICursorAcpClient Create(IJsonRpcTransport transport)
    {
        LastTransport = transport;

        return _client;
    }
}
