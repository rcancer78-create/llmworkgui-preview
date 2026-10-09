using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.IntegrationTests.OpenCode;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodeDispatchPolicyTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    private ICheckoutLockToken? _lock;
    private const string Prompt = "Synthetic project text";
    public void Dispose() { _lock?.Dispose(); _guard?.Dispose(); _db.Dispose(); }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PublicSource'")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Models SET ProviderModelId='replacement/model'")]
    [InlineData("UPDATE ProjectLocks SET ApplicationInstanceId='foreign-owner'")]
    [InlineData("UPDATE ProjectLocks SET ProcessGeneration=1")]
    [InlineData("UPDATE ProjectLocks SET CanonicalRootPath='D:\\foreign-workspace'")]
    [InlineData("UPDATE ClientRequests SET PromptHash='replacement-hash'")]
    public async Task PolicyChangedDuringBaseUrlResolutionCannotReachPromptHttp(string mutation)
    {
        var (journal, entry) = await Ready();
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            StubHttpMessageHandler.Json(request.RequestUri!.AbsolutePath == "/session" ? "{\"id\":\"native-session\"}" : "{}")));
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(http, new Uri("http://127.0.0.1:54321"), resolveBaseUrl: async token =>
        {
            if (block) { resolving.TrySetResult(); await release.Task.WaitAsync(token); }
            return new Uri("http://127.0.0.1:54321");
        });
        await client.CreateSessionAsync(new() { Title = "fixture", Directory = _db.GetWorkspacePath() });
        handler.Requests.Clear();
        block = true;
        var send = client.SendPromptAsync(entry.NativeSessionId, Request(journal, entry));
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Execute(mutation); release.TrySetResult();
        Assert.False(await send);
        Assert.Empty(handler.Requests);
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task CurrentPolicyAuthorizesOneExactPromptAndRejectsReplay()
    {
        var (journal, entry) = await Ready();
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(_db.GetWorkspacePath()));
        var request = Request(journal, entry);
        Assert.True(await journal.AuthorizePromptDispatchAsync(entry, entry.NativeSessionId, request, uri));
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, entry.NativeSessionId, request, uri));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
        Assert.Equal("17", await Scalar("SELECT json_extract(NormalizedRedactedPayloadJson,'$.processGeneration') FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(18)]
    public async Task UnknownOrForeignGenerationDoesNotConsumeAuthority(long generation)
    {
        var (journal, original) = await Ready();
        var entry = original with { ProcessGeneration = generation };
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(_db.GetWorkspacePath()));
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, entry.NativeSessionId, Request(journal, entry), uri));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
        Assert.True(await journal.AuthorizePromptDispatchAsync(original, original.NativeSessionId, Request(journal, original), uri));
    }

    [Fact]
    public async Task ReplacementEntryAndMatchingReplacementLockCannotOverrideAdmittedGeneration()
    {
        var (journal, admitted) = await Ready();
        await Execute("UPDATE ProjectLocks SET ProcessGeneration=18");
        var replacement = admitted with { ProcessGeneration = 18 };
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(_db.GetWorkspacePath()));
        Assert.False(await journal.AuthorizePromptDispatchAsync(replacement, replacement.NativeSessionId, Request(journal, replacement), uri));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
        await Execute("UPDATE ProjectLocks SET ProcessGeneration=17");
        Assert.True(await journal.AuthorizePromptDispatchAsync(admitted, admitted.NativeSessionId, Request(journal, admitted), uri));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"processGeneration\":18}")]
    public async Task MissingMalformedOrForeignAdmissionGenerationCannotAuthorize(string payload)
    {
        var (journal, entry) = await Ready();
        await Execute("UPDATE ExecutionEvents SET NormalizedRedactedPayloadJson='" + payload + "' WHERE EventKind='OpenCodeAdmission'");
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(_db.GetWorkspacePath()));
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, entry.NativeSessionId, Request(journal, entry), uri));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("model")]
    [InlineData("session")]
    [InlineData("directory")]
    [InlineData("agent")]
    public async Task ReplacementPromptModelSessionOrDirectoryCannotConsumeAuthority(string field)
    {
        var (journal, entry) = await Ready();
        var request = Request(journal, entry);
        if (field == "prompt") request = request with { Prompt = "replacement" };
        if (field == "model") request = request with { Model = "replacement/model" };
        if (field == "agent") request = request with { Agent = "replacement" };
        var session = field == "session" ? "foreign-session" : entry.NativeSessionId;
        var root = field == "directory" ? _db.GetWorkspacePath("foreign") : _db.GetWorkspacePath();
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(root));
        Assert.False(await journal.AuthorizePromptDispatchAsync(entry, session, request, uri));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
    }

    [Fact]
    public async Task AuditFailureDoesNotConsumeDispatchAuthority()
    {
        var (journal, entry) = await Ready();
        var request = Request(journal, entry);
        var uri = new Uri("http://127.0.0.1:54321/session/native-session/prompt_async?directory=" + Uri.EscapeDataString(_db.GetWorkspacePath()));
        await Execute("CREATE TRIGGER RejectTransportAudit BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='OpenCodeTransport' BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => journal.AuthorizePromptDispatchAsync(entry, entry.NativeSessionId, request, uri));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeTransport'"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    private async Task<(SqliteOpenCodeExecutionJournal, OpenCodeJournalEntry)> Ready()
    {
        await _db.InitializeAsync(); await _db.SeedRouteChainAsync();
        await Execute("UPDATE Models SET ProviderModelId='provider/native-model'");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(_db.Factory, TimeProvider.System, _guard);
        var route = Assert.Single(await journal.ListRoutesAsync());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Prompt)));
        var entry = await journal.BeginAsync("project-1", _db.GetWorkspacePath(), "native-session", route, "fixture-request", hash, 17);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard, TimeProvider.System);
        _lock = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 17);
        await journal.MarkRunningAsync(entry);
        return (journal, entry);
    }
    private static OpenCodePromptRequest Request(SqliteOpenCodeExecutionJournal journal, OpenCodeJournalEntry entry) =>
        new() { Prompt = Prompt, Model = entry.Route.NativeModelId,
            DispatchAuthorization = (native, request, uri, token) => journal.AuthorizePromptDispatchAsync(entry, native, request, uri, token) };
    private async Task Execute(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private async Task<string?> Scalar(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync()); }
}
