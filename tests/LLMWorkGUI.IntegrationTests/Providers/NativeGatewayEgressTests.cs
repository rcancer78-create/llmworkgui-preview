using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Reviews;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.GrokBot;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

/// <summary>Real SQLite, consent authority, sealed gateway/adapter/transport and local Node.
/// The transport script is an owned synthetic fixture; no desktop session, network or quota.</summary>
public sealed class NativeGatewayEgressTests
{
    [Theory]
    [InlineData(DataClassification.PublicSource)]
    [InlineData(DataClassification.PrivateSource)]
    [InlineData(DataClassification.Restricted)]
    public async Task ApprovedExactSanitizedEnvelopeReachesRealTransportAndDurableAudit(DataClassification classification)
    {
        using var f = await Fixture.Create(classification);
        var request = f.Request with { Prompt = "--review\napi_key=synthetic-egress-secret\nПроверь этот фрагмент." };
        var preview = await f.Egress.PrepareAsync(request);
        Assert.Equal(0, f.FactoryCalls); Assert.False(File.Exists(f.Marker));
        Assert.Equal(2, preview.Fragments.Count);
        var wire = string.Concat(preview.Fragments.Select(p => p.Content));
        Assert.Contains("# Conversation", wire);
        Assert.DoesNotContain("synthetic-egress-secret", wire);
        f.Approve(preview);
        var result = await f.Turns.ExecuteAsync(request with { EgressPreviewId = preview.Id });
        Assert.Equal(ExecutionState.Succeeded, result.State); Assert.Equal("review answer", result.Content);
        using var sent = JsonDocument.Parse(await File.ReadAllTextAsync(f.Marker));
        Assert.Equal(wire, sent.RootElement.GetProperty("prompt").GetString());
        Assert.True(DateTimeOffset.Parse(sent.RootElement.GetProperty("notAfterUtc").GetString()!) <= preview.ExpiresAtUtc);
        Assert.Equal(1, f.FactoryCalls);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(2L, await f.Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind LIKE 'NativeEgress%'"));
        using var audit = JsonDocument.Parse((string)(await f.Sql("SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE EventKind='NativeEgressTransportAuthorized'"))!);
        Assert.Equal(preview.Id, audit.RootElement.GetProperty("consentId").GetGuid());
        Assert.Equal(Hash(wire), audit.RootElement.GetProperty("wireSha256").GetString());
        Assert.Equal(f.Guard.InstanceId, audit.RootElement.GetProperty("authorityInstanceId").GetString());
        Assert.Equal(2, audit.RootElement.GetProperty("fragments").GetArrayLength());
        Assert.False(audit.RootElement.GetProperty("nativeIdentityConfirmed").GetBoolean());
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE NormalizedRedactedPayloadJson LIKE '%synthetic-egress-secret%' OR NormalizedRedactedPayloadJson LIKE '%Проверь%'"));
        Assert.Equal(Hash(preview.Fragments[1].Content), await f.Sql("SELECT PromptHash FROM ClientRequests"));
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(request with { EgressPreviewId = preview.Id }));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM Executions")); Assert.Equal(1, f.FactoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrPartialConsentCannotCreateExecutionOrConstructGateway(bool partial)
    {
        using var f = await Fixture.Create();
        var request = f.Request;
        if (partial)
        {
            var preview = await f.Egress.PrepareAsync(request);
            f.Egress.ApproveFragment(preview.Id, preview.Fragments[0].Id, preview.Fragments[0].ContentSha256);
            request = request with { EgressPreviewId = preview.Id };
        }
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => f.Turns.ExecuteAsync(request));
        Assert.Equal(0, f.FactoryCalls); Assert.False(File.Exists(f.Marker));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("binding")]
    [InlineData("expiry")]
    [InlineData("profile")]
    public async Task ChangedInputOrDestinationInvalidatesConsentBeforeAdmission(string change)
    {
        using var f = await Fixture.Create();
        var preview = await f.Egress.PrepareAsync(f.Request); f.Approve(preview);
        var request = f.Request with { EgressPreviewId = preview.Id };
        if (change == "prompt") request = request with { Prompt = "changed" };
        if (change == "binding") request = request with { ExpectedBinding = request.ExpectedBinding! with { NativeAccountId = "other" } };
        if (change == "expiry") f.Clock.Advance(TimeSpan.FromMinutes(6));
        if (change == "profile") await f.Sql("UPDATE ProviderProfiles SET ExecutablePath='changed' WHERE Backend='NativeGateway'");
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(request));
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(f.Request with { EgressPreviewId = preview.Id }));
        Assert.Equal(0, f.FactoryCalls); Assert.False(File.Exists(f.Marker));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("profile")]
    [InlineData("class")]
    [InlineData("health")]
    public async Task ChangeAfterWriterAcquisitionRefusesActualSendAndReleasesLocalOwnership(string change)
    {
        using var f = await Fixture.Create();
        var preview = await f.Egress.PrepareAsync(f.Request); f.Approve(preview);
        f.Locks.AfterAcquire = async () =>
        {
            if (change == "expiry") f.Clock.Advance(TimeSpan.FromMinutes(6));
            if (change == "profile") await f.Sql("UPDATE ProviderProfiles SET BaseUrl='https://changed.invalid' WHERE Backend='NativeGateway'");
            if (change == "class") await f.Sql("UPDATE Projects SET DataClassification='PublicSource'");
            if (change == "health") await f.Sql("UPDATE Accounts SET AuthState='Unknown' WHERE ProviderProfileId LIKE 'llmgateway-provider-%'");
        };
        var result = await f.Turns.ExecuteAsync(f.Request with { EgressPreviewId = preview.Id });
        Assert.Equal(ExecutionState.Failed, result.State); Assert.False(result.RequiresReconciliation);
        Assert.False(File.Exists(f.Marker));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM HealthEvents"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='NativeEgressTransportAuthorized'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogKindDoesNotSubstituteForRegisteredAdapterOrConcreteTransport(bool fakeTransport)
    {
        using var f = await Fixture.Create(fakeTransport: fakeTransport);
        var preview = await f.Egress.PrepareAsync(f.Request); f.Approve(preview);
        if (!fakeTransport)
            f.ReturnedGateway = new LlmGateway(f.Store, [new GrokBotProviderAdapter(f.Transport, new ExecutableResolver())], new());
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(f.Request with { EgressPreviewId = preview.Id }));
        Assert.False(File.Exists(f.Marker));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackAdmissionAndCannotReuseConsent()
    {
        using var f = await Fixture.Create();
        var preview = await f.Egress.PrepareAsync(f.Request); f.Approve(preview);
        await f.Sql("CREATE TRIGGER refuse_consent BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='NativeEgressConsentReserved' BEGIN SELECT RAISE(ABORT,'synthetic refusal'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Turns.ExecuteAsync(f.Request with { EgressPreviewId = preview.Id }));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions")); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions"));
        Assert.False(File.Exists(f.Marker));
        await f.Sql("DROP TRIGGER refuse_consent");
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(f.Request with { EgressPreviewId = preview.Id }));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    [Theory]
    [InlineData(DataClassification.PublicSource)]
    [InlineData(DataClassification.PrivateSource)]
    [InlineData(DataClassification.Restricted)]
    public async Task ProjectFacadeCannotDispatchGrokBotWithoutLocalConsent(DataClassification classification)
    {
        using var f = await Fixture.Create(classification);
        var facade = new ProjectNativeGateway(f.Catalog, f.Turns, f.Request.ProjectId, f.Request.RootPath, TimeSpan.FromSeconds(10));
        var request = new ChatRequest { Model = f.Request.RouteId, Messages = [ChatMessage.User(f.Request.Prompt)] };
        Assert.Single(await facade.GetModelsAsync()); // Catalog visibility grants no dispatch permission.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => facade.CompleteAsync(request));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => { await foreach (var _ in facade.StreamAsync(request)) { } });
        Assert.Equal(0, f.FactoryCalls); Assert.False(File.Exists(f.Marker));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ExecutionEvents"));
    }
    [Theory]
    [InlineData("prompt")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task FailedPreflightOrCancelledQueueSpendsConsent(string failure)
    {
        using var f = await Fixture.Create();
        var preview = await f.Egress.PrepareAsync(f.Request); f.Approve(preview);
        var valid = f.Request with { EgressPreviewId = preview.Id };
        var invalid = failure switch { "prompt" => valid with { Prompt = "" }, "timeout" => valid with { Timeout = TimeSpan.Zero }, _ => valid };
        using var cancel = new CancellationTokenSource(); if (failure == "cancel") cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Turns.ExecuteAsync(invalid, cancel.Token));
        await Assert.ThrowsAsync<EgressApprovalException>(() => f.Turns.ExecuteAsync(valid));
        Assert.False(File.Exists(f.Marker)); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabase _db = new();
        private LlmGateway _gateway = null!;
        public ApplicationInstanceGuard Guard = null!;
        public Clock Clock { get; } = new();
        public Store Store { get; } = new();
        public IGrokBotReviewTransport Transport = null!;
        public LlmGateway? ReturnedGateway;
        public NativeGatewayEgressCoordinator Egress = null!;
        public NativeGatewayTurnService Turns = null!;
        public Locks Locks = null!;
        public NativeGatewayTurnRequest Request = null!;
        public INativeGatewayRouteCatalog Catalog => new SqliteNativeGatewayRouteCatalog(_db.Factory, new SensitiveDataFilter(), Clock);
        public int FactoryCalls;
        public string Marker => Path.Combine(_db.Root, "sent.json");
        public static async Task<Fixture> Create(DataClassification classification = DataClassification.Restricted, bool fakeTransport = false)
        {
            var f = new Fixture();
            try
            {
                await f._db.InitializeAsync(); await f._db.SeedRouteChainAsync();
                Directory.CreateDirectory(f._db.GetWorkspacePath());
                f.Guard = new ApplicationInstanceGuard(f._db.Root);
                var script = Path.Combine(f._db.Root, "fixture.mjs");
                await File.WriteAllTextAsync(script, """
                    import fs from 'node:fs'; import readline from 'node:readline';
                    const lines=readline.createInterface({input:process.stdin});
                    lines.once('line', line => { fs.writeFileSync('sent.json', line); console.log(JSON.stringify({text:'review answer',cleanupPending:false})); lines.close(); process.stdin.destroy(); });
                    """);
                f.Transport = fakeTransport ? new FakeTransport() : new GrokBotReviewTransport(script);
                var adapter = new GrokBotProviderAdapter(f.Transport, new ExecutableResolver());
                f._gateway = new LlmGateway(f.Store, [adapter], new GatewayOptions { DiscoverProfiles = false });
                var imported = await new SqliteGatewayCatalogImportService(() => f._gateway, new(new SensitiveDataFilter()), f._db.Factory, f.Guard, f.Clock).ImportAsync();
                var route = Assert.Single(imported.Routes);
                await f.Sql($"UPDATE Projects SET DataClassification='{classification}'; UPDATE ProviderProfiles SET IsEnabled=1,MaxDataClass='Restricted' WHERE Backend='NativeGateway'; UPDATE Accounts SET IsEnabled=1,AuthState='Valid',Health='Healthy' WHERE ProviderProfileId LIKE 'llmgateway-provider-%'; UPDATE Models SET IsEnabled=1,CapabilityState='Supported',Health='Healthy' WHERE Backend='NativeGateway'; UPDATE Routes SET IsEnabled=1,Health='Healthy',MaxDataClass='Restricted' WHERE Backend='NativeGateway'");
                f.Request = new("project-1", f._db.GetWorkspacePath(), route.Id, Guid.NewGuid().ToString("D"), "review text")
                { ExpectedBinding = new(route.Binding.ProviderProfileId, route.Binding.AccountId, route.Binding.ModelId, "grokbot-default", "grok-bot") };
                var authority = new EgressApprovalService(new SqliteProjectRepository(f._db.Factory), new SqliteRouteRepository(f._db.Factory),
                    new SqliteProviderProfileRepository(f._db.Factory), new WorkflowSecretScanner(), f.Clock, f.Guard);
                f.Egress = new(f._db.Factory, authority, f.Guard, () => { f.FactoryCalls++; return f.ReturnedGateway ?? f._gateway; }, adapter, f.Transport, f.Clock);
                f.Locks = new(new CheckoutLockService(new SqliteProjectLockRepository(f._db.Factory), f.Guard, f.Clock));
                var health = new HealthCenterService(new SqliteHealthStateRepository(f._db.Factory), new SqliteHealthEventRepository(f._db.Factory));
                f.Turns = new(() => throw new InvalidOperationException("Approved gateway must be retained"), f._db.Factory, f.Guard, f.Locks,
                    health, new SensitiveDataFilter(), f.Clock, egress: f.Egress);
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        public void Approve(EgressPreview preview) { foreach (var p in preview.Fragments) Egress.ApproveFragment(preview.Id, p.Id, p.ContentSha256); }
        public async Task<object?> Sql(string sql)
        {
            await using var c = await _db.Factory.OpenConnectionAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync();
        }
        public void Dispose() { ReturnedGateway?.Dispose(); _gateway?.Dispose(); Locks?.Dispose(); Guard?.Dispose(); _db.Dispose(); }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private long _offset;
        public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(Interlocked.Read(ref _offset));
        public void Advance(TimeSpan value) => Interlocked.Add(ref _offset, value.Ticks);
    }
    private sealed class Store : IAccountStore
    {
        private readonly AccountProfile _account = new() { Id = "grokbot-default", DisplayName = "Synthetic review account", Provider = ProviderKind.GrokBot, Enabled = true, IsActive = true };
        public IReadOnlyList<AccountProfile> GetAll() => [_account.Clone()];
        public AccountProfile? Find(string id) => id == _account.Id ? _account.Clone() : null;
        public Task AddAsync(AccountProfile p, CancellationToken t = default) => throw new NotSupportedException();
        public Task UpdateAsync(AccountProfile p, CancellationToken t = default) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken t = default) => throw new NotSupportedException();
        public Task SelectAsync(string id, CancellationToken t = default) => throw new NotSupportedException();
    }
    private sealed class Locks(ICheckoutLockService inner) : ICheckoutLockService, IDisposable
    {
        private readonly List<ICheckoutLockToken> _owned = [];
        public Func<Task>? AfterAcquire;
        public bool RequiresWriterLock(string? mode) => inner.RequiresWriterLock(mode);
        public bool RequiresWriterLock(WorkflowRole role, string? mode) => inner.RequiresWriterLock(role, mode);
        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string project, string root, string execution,
            long generation, string? mode, WorkflowRole role = WorkflowRole.Unknown, CancellationToken token = default) =>
            inner.AcquireLockForExecutionAsync(project, root, execution, generation, mode, role, token);
        public async Task<ICheckoutLockToken> AcquireWriterLockAsync(string project, string root, string execution, long version, CancellationToken token = default)
        {
            var owned = await inner.AcquireWriterLockAsync(project, root, execution, version, token); _owned.Add(owned);
            if (AfterAcquire is { } action) await action(); return owned;
        }
        public void Dispose() { foreach (var item in _owned) item.Dispose(); }
    }
    private sealed class FakeTransport : IGrokBotReviewTransport
    {
        public Task<bool> CheckSessionAsync(string node, CancellationToken token) => throw new InvalidOperationException("Must not probe");
        public Task<(string Text, bool CleanupPending)> ReviewAsync(string node, string prompt, CancellationToken token) => throw new InvalidOperationException("Must not dispatch");
    }
}
