using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    private readonly ITestOutputHelper _output;
    public MirasimEgressTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public async Task UnusedHostedMirasimDoesNotResolveTheLifecycleOrConstructItsTransport()
    {
        var constructions = 0;
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddMirasimBackend();
            services.AddSingleton<IMirasimSessionLifecycleService>(_ =>
            {
                Interlocked.Increment(ref constructions);
                throw new InvalidOperationException("An unused Mirasim lifecycle must remain lazy.");
            });
        }).Build();
        await host.StartAsync(); await host.StopAsync();
        Assert.Equal(0, constructions);
    }

    [Fact]
    public async Task LocalPolicyRefusalBeforeHttpReturnsItsReservedNativeSessionCapacity()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxSessions: 1);
        await f.Db.Change("class");
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
        Assert.Empty(f.Server.Paths);
        await f.Db.Sql("UPDATE Projects SET DataClassification='PrivateSource'");
        await f.CreateSession();
        Assert.Single(f.Server.Paths);
        await f.Host.StopAsync();
    }

    [Fact]
    public async Task HostStoppingCancelsActualPendingHttpButRetainsUnconfirmedWriterAndJournal()
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession(); f.Server.DeferTurn = true;
        var sending = f.Service.ExecuteTurnAsync(f.Request(binding));
        await f.Server.TurnReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync(deadline.Token));
            Assert.True(sending.IsCompleted);
            Assert.Equal(MirasimTurnStatus.Ambiguous, (await sending).Status);
            Assert.Equal("Ambiguous", await f.Db.Sql("SELECT State FROM Executions"));
            Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
            Assert.Single(f.Server.Paths.Where(path => path.EndsWith("/turns", StringComparison.Ordinal)));
        }
        finally
        {
            f.Server.Release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task HostStopRefusesToReportSuccessWithAnAlreadyObservedRunningNativeTurn()
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession(); f.Server.TurnPhase = "running";
        Assert.Equal(MirasimTurnStatus.Running, (await f.Service.ExecuteTurnAsync(f.Request(binding))).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync());
        Assert.Equal("Running", await f.Db.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task CleanHostStopRetiresAllNewSessionAndPromptAdmissionBeforeHttpOrSql()
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        await f.Host.StopAsync();
        var requests = f.Server.Paths.Count;
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
        var result = await f.Service.ExecuteTurnAsync(f.Request(binding));
        Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
        Assert.Equal(requests, f.Server.Paths.Count);
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task TerminalCommitAndWriterReleaseAllowActualHostStop()
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request(binding))).Status);
        await f.Host.StopAsync();
        Assert.Equal("Succeeded", await f.Db.Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task SessionCapacityIsReservedBeforeAnotherNativeSessionIsCreated()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxSessions: 2);
        await f.CreateSession(); await f.CreateSession();
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
        Assert.Equal(2, f.Server.Paths.Count);
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM Sessions"));
    }

    [Fact]
    public async Task ExecutionCapacityRetainsUnresolvedAdmissionsWithoutCreatingMoreSqlRows()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxExecutions: 1);
        var first = await f.CreateSession(); var second = await f.CreateSession();
        await f.Db.Sql("UPDATE Accounts SET MaxConcurrentExecutions=10"); f.Server.TurnPhase = "running";
        Assert.Equal(MirasimTurnStatus.Running, (await f.Service.ExecuteTurnAsync(f.Request(first, "first", "plan"))).Status);
        var result = await f.Service.ExecuteTurnAsync(f.Request(second, "second", "plan"));
        Assert.NotEqual(MirasimTurnStatus.Running, result.Status);
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Single(f.Server.Paths.Where(path => path.EndsWith("/turns", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FailedTerminalCommitKeepsCapacityUntilExactOwnedReconcileCompletes()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxExecutions: 1);
        var first = await f.CreateSession(); var second = await f.CreateSession();
        await f.Db.Sql("UPDATE Accounts SET MaxConcurrentExecutions=10");
        await f.Db.Sql("CREATE TRIGGER refuse_mirasim_terminal BEFORE UPDATE OF State ON Executions WHEN NEW.State='Succeeded' BEGIN SELECT RAISE(ABORT,'synthetic terminal failure'); END");
        var pending = await f.Service.ExecuteTurnAsync(f.Request(first, "first", "plan"));
        Assert.Equal(MirasimTurnStatus.Ambiguous, pending.Status);
        var refused = await f.Service.ExecuteTurnAsync(f.Request(second, "second", "plan"));
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, refused.Status);
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
        await f.Db.Sql("DROP TRIGGER refuse_mirasim_terminal");
        Assert.True((await f.Service.ReconcileTurnAsync(first.SessionKey, pending.TurnId)).IsTerminal);
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request(second, "second", "plan"))).Status);
        Assert.Equal(2L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
        await f.Host.StopAsync();
    }

    [Fact]
    public async Task ConfirmedTerminalTurnsRetireCapacityOnlyAfterPhysicalRelease()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxExecutions: 1);
        var binding = await f.CreateSession();
        f.Db.Locks.RefuseRelease = true;
        var first = await f.Service.ExecuteTurnAsync(f.Request(binding, "first"));
        Assert.Equal(MirasimTurnStatus.Ambiguous, first.Status);
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        f.Db.Locks.RefuseRelease = false;
        Assert.True((await f.Service.ReconcileTurnAsync(binding.SessionKey, first.TurnId)).IsTerminal);
        for (var turn = 0; turn < 3; turn++)
            Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request(binding, $"completed-{turn}"))).Status);
        Assert.Equal(4L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await f.Host.StopAsync();
    }

    [Fact]
    public async Task UnknownNativeSessionCreationKeepsItsReservedCapacityAndMakesStopFail()
    {
        await using var f = await OwnedHostFixture.Create(_output, maxSessions: 1);
        f.Server.DeferSession = true;
        using var caller = new CancellationTokenSource();
        var creating = f.CreateSession(caller.Token);
        await f.Server.SessionReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => creating);
        f.Server.DeferSession = false;
        try
        {
            await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
            await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync());
            Assert.Single(f.Server.Paths);
            Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
        }
        finally { f.Server.Release.TrySetResult(); }
    }

    private sealed class OwnedHostFixture : IAsyncDisposable
    {
        public Fixture Db = null!;
        public OwnedMirasimHttpServer Server = null!;
        public IHost Host = null!;
        public IMirasimSessionLifecycleService Service = null!;
        private ITestOutputHelper _output = null!;
        public static async Task<OwnedHostFixture> Create(ITestOutputHelper output, int maxSessions = 16, int maxExecutions = 16)
        {
            var f = new OwnedHostFixture { _output = output };
            try
            {
                f.Db = await Fixture.Create(); f.Server = new();
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mirasim:Hostname"] = "127.0.0.1", ["Mirasim:Port"] = f.Server.Port.ToString(),
                    ["Mirasim:MaxRetainedSessions"] = maxSessions.ToString(), ["Mirasim:MaxRetainedExecutions"] = maxExecutions.ToString()
                }).Build();
                f.Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().ConfigureServices(services =>
                {
                    services.AddSingleton<IApplicationInstanceGuard>(f.Db.Guard);
                    services.AddSingleton<ICheckoutLockService>(f.Db.Locks);
                    services.AddSingleton<IMirasimEgressPolicy>(new SqliteMirasimEgressPolicy(f.Db.Factory, f.Db.Guard, TimeProvider.System));
                    services.AddSingleton<IMirasimExecutionJournal>(new SqliteMirasimExecutionJournal(f.Db.Factory, f.Db.Guard, TimeProvider.System));
                    services.AddMirasimBackend(config);
                }).Build();
                await f.Host.StartAsync();
                f.Service = f.Host.Services.GetRequiredService<IMirasimSessionLifecycleService>();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<MirasimSessionBinding> CreateSession(CancellationToken token = default)
        {
            Server.RecordPhase("session-create-invoked");
            try
            {
                return await Service.CreateSessionAsync("synthetic-instance", "codex", "model", Db.Request.CanonicalRootPath,
                    cancellationToken: token, projectContext: Db.Context).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not MirasimEgressPolicyException && !token.IsCancellationRequested)
            {
                try
                {
                    Server.RecordPhase("session-create-failed");
                    _output.WriteLine("Owned Mirasim session creation failure: " + Server.DiagnosticSnapshot(error.GetType().Name));
                }
                catch (Exception) { /* A diagnostic sink must not replace the original transport exception. */ }
                throw;
            }
        }
        public MirasimTurnRequest Request(MirasimSessionBinding binding, string id = "owned-host-execution", string mode = "Write") =>
            Db.Request with { SessionKey = binding.SessionKey, ExecutionId = id, ExecutionMode = mode };
        public async ValueTask DisposeAsync()
        {
            if (Server is not null) await Server.DisposeAsync().ConfigureAwait(false);
            Host?.Dispose();
            if (Db is not null)
            {
                // Teardown only, after all ownership assertions and after this synthetic server ended.
                // This permits disposal of the fixture's real mutex tokens; it is not product recovery evidence.
                await Db.Sql("UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Closed',ActiveExecutionId=NULL").ConfigureAwait(false);
                Db.Dispose();
            }
        }
    }

    // Owns only an ephemeral loopback socket; no native process, model call or personal credentials.
    private sealed class OwnedMirasimHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentBag<Task> _requests = [];
        private readonly Task _accepting;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly object _traceGate = new();
        private readonly Queue<PhaseSample> _trace = new();
        private int _session; private int _turn;
        public int Port { get; }
        public ConcurrentQueue<string> Paths { get; } = new();
        public TaskCompletionSource SessionReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TurnReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeferSession; public bool DeferTurn;
        public string TurnPhase = "done";
        public int TurnHttpStatus = 200;
        public bool OverrideTurnIdentity;
        public string? TurnIdentityOverride;
        public string? RedirectLocation;
        public string? FixedResponseBody;
        public string? ContinueSessionKey;
        public OwnedMirasimHttpServer()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            RecordPhase("server-initialized"); _accepting = AcceptAsync();
        }
        public void RecordPhase(string phase)
        {
            lock (_traceGate)
            {
                ThreadPool.GetAvailableThreads(out var workers, out var completionPorts);
                if (_trace.Count == 64) _trace.Dequeue();
                _trace.Enqueue(new(phase, Math.Round(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, 3),
                    Environment.CurrentManagedThreadId, SynchronizationContext.Current?.GetType().FullName,
                    ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, workers, completionPorts));
            }
        }
        public string DiagnosticSnapshot(string errorType)
        {
            PhaseSample[] phases;
            lock (_traceGate) phases = _trace.ToArray();
            return JsonSerializer.Serialize(new { errorType, phases });
        }
        private sealed record PhaseSample(string Phase, double ElapsedMilliseconds, int ThreadId, string? ContextType,
            int PoolThreads, long PendingWorkItems, int AvailableWorkers, int AvailableCompletionPorts);
        private async Task AcceptAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    RecordPhase("accept-awaited");
                    var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                    RecordPhase("socket-accepted");
                    _requests.Add(RespondAsync(client));
                }
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested && error is OperationCanceledException or SocketException) { }
        }
        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await using var streamLease = stream.ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false); if (line is null) return;
                    var path = line.Split(' ')[1]; int length = 0;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false)))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    RecordPhase("headers-read");
                    if (length > 0) await reader.ReadBlockAsync(new char[length].AsMemory(), _shutdown.Token).ConfigureAwait(false);
                    RecordPhase("body-read");
                    Paths.Enqueue(path);
                    if (RedirectLocation is { } location)
                    {
                        var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(redirect, _shutdown.Token).ConfigureAwait(false);
                        return;
                    }
                    var isSession = path == "/api/sessions";
                    string body;
                    if (FixedResponseBody is { } fixedBody) body = fixedBody;
                    else if (isSession)
                    {
                        var session = $"owned-session-{Interlocked.Increment(ref _session)}";
                        SessionReceived.TrySetResult();
                        if (DeferSession)
                        {
                            RecordPhase("response-deferred");
                            await Release.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                            RecordPhase("response-released");
                        }
                        body = JsonSerializer.Serialize(new { sessionKey = session, routeMode = "manualOnly" });
                    }
                    else if (path.EndsWith("/continue", StringComparison.Ordinal))
                        body = JsonSerializer.Serialize(new { sessionKey = ContinueSessionKey ?? Uri.UnescapeDataString(path.Split('/')[3]),
                            routeMode = "manualOnly" });
                    else
                    {
                        var parts = path.Split('/'); var session = parts[3];
                        var turn = path.EndsWith("/turns", StringComparison.Ordinal)
                            ? $"owned-turn-{Interlocked.Increment(ref _turn)}" : parts[5];
                        TurnReceived.TrySetResult();
                        if (DeferTurn)
                        {
                            RecordPhase("response-deferred");
                            await Release.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                            RecordPhase("response-released");
                        }
                        body = JsonSerializer.Serialize(new { sessionKey = session,
                            turnId = OverrideTurnIdentity ? TurnIdentityOverride : turn, phase = TurnPhase,
                            terminal = TurnPhase == "done", model = "model", response = "synthetic response" });
                    }
                    var bytes = Encoding.ASCII.GetBytes(body);
                    RecordPhase("response-prepared");
                    var status = isSession ? 200 : TurnHttpStatus;
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Synthetic\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _shutdown.Token).ConfigureAwait(false);
                    RecordPhase("headers-written");
                    await stream.WriteAsync(bytes, _shutdown.Token).ConfigureAwait(false);
                    RecordPhase("body-written");
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
                finally { RecordPhase("socket-ended"); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel(); Release.TrySetResult(); _listener.Stop();
            await _accepting.ConfigureAwait(false); await Task.WhenAll(_requests.ToArray()).ConfigureAwait(false); _shutdown.Dispose();
        }
    }
}
