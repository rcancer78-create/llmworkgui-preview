using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

public sealed class CursorDispatchGateTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private const string Prompt = "Synthetic private prompt must never enter local audit.";
    private const long ProcessGeneration = 42;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'", "ask")]
    [InlineData("UPDATE Projects SET RootPath=RootPath || '-changed'", "ask")]
    [InlineData("UPDATE Routes SET IsEnabled=0", "ask")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PublicSource'", "ask")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'", "ask")]
    [InlineData("UPDATE Accounts SET CooldownUntilUtc='2999-01-01T00:00:00Z'", "ask")]
    [InlineData("UPDATE Accounts SET MaxConcurrentExecutions=1", "ask")]
    [InlineData("UPDATE Models SET ProviderModelId='changed-native-model'", "ask")]
    [InlineData("UPDATE Sessions SET NativeSessionId='changed-native-session'", "ask")]
    [InlineData("UPDATE ClientRequests SET PromptHash='changed'", "ask")]
    [InlineData("UPDATE ProjectLocks SET ApplicationInstanceId='foreign-owner'", "agent")]
    [InlineData("UPDATE ProjectLocks SET ProcessGeneration=1", "agent")]
    public async Task CurrentPolicyAndOwnerChangedDuringModelPreparationRefuseNativePrompt(string mutation, string mode)
    {
        var (journal, entry, request) = await ReadyAsync(mode, reserveParallel: mutation.Contains("MaxConcurrentExecutions", StringComparison.Ordinal));
        await using var writer = await AcquireWriterAsync(entry, mode);
        request = Bind(request, journal, entry, writer?.LockId);
        var methods = new ConcurrentQueue<string>();
        var observations = new List<bool>();
        await using var harness = new CursorAcpPipeHarness();
        harness.AsyncFrameHandler = async frame =>
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.GetProperty("method").GetString() == "session/set_model") await ExecuteAsync(mutation);
            return Reply(frame, methods);
        };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);

        var result = await client.PromptAsync(request with { DispatchObserver = observations.Add });

        Assert.DoesNotContain("session/prompt", methods);
        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { false }, observations);
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
        Assert.Equal(entry.ExecutionId, await ScalarAsync("SELECT ActiveExecutionId FROM Sessions"));
    }

    [Fact]
    public async Task PolicyIsCheckedAfterWaitingBehindAnotherStdioWrite()
    {
        var (journal, entry, request) = await ReadyAsync("ask");
        request = Bind(request, journal, entry);
        var methods = new ConcurrentQueue<string>();
        await using var harness = new CursorAcpPipeHarness();
        await using var blocker = new BlockingWriteStream(harness.TransportOutput);
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, blocker);
        harness.AsyncFrameHandler = async frame =>
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.GetProperty("method").GetString() == "session/set_mode")
            {
                blocker.Arm();
                await transport.SendNotificationAsync("fixture/write-blocker");
            }
            return Reply(frame, methods);
        };
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        var pending = client.PromptAsync(request);
        await blocker.Started.Task.WaitAsync(Bound);
        await ExecuteAsync("UPDATE Projects SET DataClassification='Restricted'");
        blocker.Release.TrySetResult();

        var result = await pending.WaitAsync(Bound);

        Assert.DoesNotContain("session/prompt", methods);
        Assert.False(result.IsSuccess);
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
    }

    [Fact]
    public async Task UnknownTransportCannotBypassProjectDispatchCallback()
    {
        var (journal, entry, request) = await ReadyAsync("ask");
        var methods = new ConcurrentQueue<string>();
        await using var harness = new CursorAcpPipeHarness { FrameHandler = frame => Reply(frame, methods) };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(new UngatedTransport(transport));
        Assert.True((await client.InitializeAsync()).IsReady);

        var result = await client.PromptAsync(Bind(request, journal, entry));

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "initialize" }, methods.ToArray());
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("agent")]
    public async Task ValidPreparedRequestGetsOneDurableGrantAndCannotReplay(string mode)
    {
        var (journal, entry, request) = await ReadyAsync(mode);
        await using var writer = await AcquireWriterAsync(entry, mode);
        var methods = new ConcurrentQueue<string>();
        await using var harness = new CursorAcpPipeHarness { FrameHandler = frame => Reply(frame, methods) };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        request = Bind(request, journal, entry, writer?.LockId);

        Assert.True((await client.PromptAsync(request)).IsSuccess);
        Assert.False((await client.PromptAsync(request)).IsSuccess);

        Assert.Single(methods.Where(method => method == "session/prompt"));
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
        Assert.DoesNotContain(Prompt, await ScalarAsync("SELECT COALESCE(GROUP_CONCAT(NormalizedRedactedPayloadJson),'') FROM ExecutionEvents") ?? "");
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("session")]
    [InlineData("request")]
    [InlineData("model")]
    [InlineData("mode")]
    public async Task DurableGrantRequiresExactPreparedRequestIdentity(string field)
    {
        var (journal, entry, request) = await ReadyAsync("ask");
        var forged = field switch
        {
            "prompt" => request with { Prompt = "substituted", PromptHash = CursorAcpPromptRequest.ComputePromptHash("substituted") },
            "session" => request with { SessionId = "other-native" },
            "request" => request with { ClientRequestId = Guid.NewGuid().ToString("D") },
            "model" => request with { Model = "other-model" },
            "mode" => request with { ModeId = "agent" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), forged, null, ProcessGeneration));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Fact]
    public async Task CancellationAfterDurableGrantReportsPossibleDispatchAndPreservesOwnership()
    {
        var (journal, entry, request) = await ReadyAsync("agent");
        await using var writer = await AcquireWriterAsync(entry, "agent");
        using var stop = new CancellationTokenSource();
        var observations = new List<bool>();
        var methods = new ConcurrentQueue<string>();
        await using var harness = new CursorAcpPipeHarness { FrameHandler = frame => Reply(frame, methods) };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        var bound = Bind(request, journal, entry, writer!.LockId);
        bound = bound with { BeforeDispatchCancellationToken = stop.Token, DispatchObserver = observations.Add,
            AuthorizeDispatchAsync = async (actual, token) =>
            {
                var allowed = await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), actual, writer.LockId, ProcessGeneration, token);
                if (allowed) stop.Cancel();
                return allowed;
            } };

        var result = await client.PromptAsync(bound);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { true }, observations);
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
        Assert.True(writer.IsHeld);
        Assert.Equal(entry.ExecutionId, await ScalarAsync("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await ScalarAsync("SELECT EndedAtUtc FROM Executions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTimeoutCannotReportNonDispatchBeforeInProgressGrantOutcome(bool cancelCaller)
    {
        var (journal, entry, request) = await ReadyAsync("agent");
        await using var writer = await AcquireWriterAsync(entry, "agent");
        using var caller = new CancellationTokenSource();
        var grantCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new ConcurrentQueue<bool>();
        var methods = new ConcurrentQueue<string>();
        await using var harness = new CursorAcpPipeHarness { FrameHandler = frame => Reply(frame, methods) };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(transport, new CursorAcpOptions
            { TurnHardTimeout = cancelCaller ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(1) });
        Assert.True((await client.InitializeAsync()).IsReady);
        request = request with { DispatchObserver = observations.Enqueue, AuthorizeDispatchAsync = async (actual, token) =>
        {
            var allowed = await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), actual, writer!.LockId, ProcessGeneration, token);
            using var registration = token.Register(() => gateCancellation.TrySetResult());
            grantCommitted.TrySetResult();
            // This barrier represents a durable grant whose completion has not yet reached the transport.
            await finishGate.Task;
            return allowed;
        } };
        var pending = client.PromptAsync(request, caller.Token);
        bool returnedBeforeGate;
        try
        {
            await grantCommitted.Task.WaitAsync(Bound);
            if (cancelCaller) caller.Cancel();
            await gateCancellation.Task.WaitAsync(Bound);
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(300)));
            returnedBeforeGate = pending.IsCompleted;
        }
        finally { finishGate.TrySetResult(); }
        await Record.ExceptionAsync(async () => await pending.WaitAsync(Bound));

        Assert.False(returnedBeforeGate);
        Assert.Equal(new[] { true }, observations.ToArray());
        Assert.True(writer!.IsHeld);
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
        Assert.Equal(entry.ExecutionId, await ScalarAsync("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await ScalarAsync("SELECT EndedAtUtc FROM Executions"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Dispatch_UnknownProcessGenerationIsRefusedWithoutAudit(long generation)
    {
        var (journal, entry, request) = await ReadyAsync("ask");
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), request, null, generation));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
    }

    [Fact]
    public async Task Dispatch_AuditRetainsTheAuthorizedProcessGeneration()
    {
        var (journal, entry, request) = await ReadyAsync("ask");
        Assert.True(await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), request, null, ProcessGeneration));
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch' AND json_extract(NormalizedRedactedPayloadJson,'$.processGeneration')=42"));
    }

    [Fact]
    public async Task Dispatch_StaleGenerationCannotAuthorizeTheHeldWriterLock()
    {
        var (journal, entry, request) = await ReadyAsync("agent");
        using var writer = await AcquireWriterAsync(entry, "agent");
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), request, writer!.LockId, ProcessGeneration + 1));
        Assert.True(writer.IsHeld);
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='CursorPromptDispatch'"));
    }

    private async Task<(ICursorAcpExecutionJournal Journal, CursorAcpJournalEntry Entry, CursorAcpPromptRequest Request)> ReadyAsync(string mode, bool reserveParallel = false)
    {
        await _db.InitializeAsync();
        await _db.SeedRouteChainAsync();
        await ExecuteAsync("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp',ProviderModelId='native-model'; UPDATE Routes SET Backend='CursorAcp';");
        if (reserveParallel) await ExecuteAsync("UPDATE Accounts SET MaxConcurrentExecutions=2");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        ICursorAcpExecutionJournal journal = new SqliteCursorAcpExecutionJournal(_db.Factory, TimeProvider.System, _guard);
        var route = Assert.Single(await journal.ListRoutesAsync());
        var request = CursorAcpPromptRequest.Create("native-session", Prompt, route.NativeModelId) with
            { RequireModelAcknowledgement = true, ModeId = mode };
        var entry = await journal.BeginAsync("project-1", _db.GetWorkspacePath(), request.SessionId, route, mode, request.ClientRequestId, request.PromptHash);
        if (reserveParallel)
            await journal.BeginAsync("project-1", _db.GetWorkspacePath(), "parallel-native-session", route, "ask",
                Guid.NewGuid().ToString("D"), CursorAcpPromptRequest.ComputePromptHash("synthetic parallel reservation"));
        return (journal, entry, request);
    }

    private Task<ICheckoutLockToken?> AcquireWriterAsync(CursorAcpJournalEntry entry, string mode) => mode == "agent"
        ? AcquireAsync() : Task.FromResult<ICheckoutLockToken?>(null);
    private async Task<ICheckoutLockToken?> AcquireAsync()
    {
        var execution = await ScalarAsync("SELECT Id FROM Executions");
        return await new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System)
            .AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), execution!, ProcessGeneration);
    }
    private CursorAcpPromptRequest Bind(CursorAcpPromptRequest request, ICursorAcpExecutionJournal journal,
        CursorAcpJournalEntry entry, string? lockId = null) => request with
        { AuthorizeDispatchAsync = (actual, token) => journal.AuthorizePromptDispatchAsync(entry, "project-1", _db.GetWorkspacePath(), actual, lockId, ProcessGeneration, token) };

    private static string? Reply(string frame, ConcurrentQueue<string> methods, Action<string>? beforeReply = null)
    {
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString()!;
        methods.Enqueue(method);
        beforeReply?.Invoke(method);
        if (!root.TryGetProperty("id", out var id)) return null;
        return CursorAcpIntegrationTestData.CreateResponseJson(id.GetInt32(), method == "initialize"
            ? CursorAcpIntegrationTestData.ReadHandshakeResult()
            : JsonSerializer.SerializeToElement(method == "session/prompt" ? new { stopReason = "endTurn" } : (object)new { }));
    }
    private async Task ExecuteAsync(string sql)
    { await using var c = await _db.Factory.OpenConnectionAsync(); await using var command = c.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private async Task<string?> ScalarAsync(string sql)
    { await using var c = await _db.Factory.OpenConnectionAsync(); await using var command = c.CreateCommand(); command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(); return value is null or DBNull ? null : Convert.ToString(value); }

    private sealed class UngatedTransport(IJsonRpcTransport inner) : IJsonRpcTransport
    {
        public ChannelReader<JsonRpcNotification> Notifications => inner.Notifications;
        public int MalformedFrameCount => inner.MalformedFrameCount;
        public Task<JsonRpcResponse> SendRequestAsync(string method, JsonElement? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => inner.SendRequestAsync(method, parameters, timeout, cancellationToken);
        public Task SendNotificationAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default) => inner.SendNotificationAsync(method, parameters, cancellationToken);
        public Task SendResponseAsync(JsonElement id, JsonElement? result = null, JsonRpcError? error = null, CancellationToken cancellationToken = default) => inner.SendResponseAsync(id, result, error, cancellationToken);
        public Task CloseAsync(CancellationToken cancellationToken = default) => inner.CloseAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BlockingWriteStream(Stream inner) : Stream
    {
        private int _armed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
