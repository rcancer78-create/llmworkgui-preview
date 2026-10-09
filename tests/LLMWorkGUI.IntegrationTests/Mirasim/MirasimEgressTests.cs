using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Mirasim;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LLMWorkGUI.Infrastructure.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

/// <summary>Real SQL metadata/primary guard/checkout lock/lifecycle with an owned HTTP handler, no live host.</summary>
public sealed partial class MirasimEgressTests
{
    [Theory]
    [InlineData("class")]
    [InlineData("max")]
    [InlineData("disabled")]
    [InlineData("backend")]
    [InlineData("root")]
    [InlineData("missing")]
    public async Task DirectSessionCreationChecksStoredPolicyBeforeHttp(string change)
    {
        using var f = await Fixture.Create(); await f.Change(change);
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("max")]
    [InlineData("disabled")]
    [InlineData("backend")]
    [InlineData("root")]
    [InlineData("missing")]
    public async Task DirectTurnRechecksStoredPolicyWithoutTrustingTheCaller(string change)
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        await f.Change(change);
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, result.Status);
        Assert.True(result.IsTerminal); Assert.Empty(f.Handler.Requests);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task MissingServiceCannotCreateAWorkspaceSessionOrSendPrompt()
    {
        using var f = await Fixture.Create();
        var service = new MirasimSessionLifecycleService(f.Client, Fixture.Options(), f.Locks);
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => service.CreateSessionAsync("instance", "codex", "model", f.Request.CanonicalRootPath,
            projectContext: f.Context));
        var result = await service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, result.Status); Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData(DataClassification.PublicSource)]
    [InlineData(DataClassification.PrivateSource)]
    public async Task StoredPolicyAuthorizesExactSessionAndTurnBodiesWithoutRawAuditContent(DataClassification classification)
    {
        using var f = await Fixture.Create();
        await f.Sql($"UPDATE Projects SET DataClassification='{classification}'");
        var binding = await f.CreateSession();
        Assert.Equal(f.Context, binding.ProjectContext);
        await f.Service.ContinueSessionAsync(binding);
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.Completed, result.Status); Assert.False(result.RequiresLocalCleanup);
        Assert.Equal(3, f.Handler.Requests.Count);
        var payloads = ((string)(await f.Sql("SELECT json_group_array(json(DescriptionRedacted)) FROM ActivityEvents WHERE TitleRedacted='Mirasim: локальная policy проверена'"))!)!;
        using var audit = JsonDocument.Parse(payloads);
        Assert.Equal(3, audit.RootElement.GetArrayLength());
        var hashes = audit.RootElement.EnumerateArray().Select(e => e.GetProperty("bodySha256").GetString()).ToArray();
        foreach (var body in f.Handler.Requests) Assert.Contains(Hash(body), hashes);
        foreach (var entry in audit.RootElement.EnumerateArray())
        { Assert.False(entry.GetProperty("nativeIdentityConfirmed").GetBoolean()); Assert.Equal(classification.ToString(), entry.GetProperty("classification").GetString()); }
        Assert.DoesNotContain(f.Request.Prompt, payloads); Assert.DoesNotContain(f.Request.CanonicalRootPath, payloads);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("max")]
    [InlineData("disabled")]
    [InlineData("backend")]
    [InlineData("root")]
    [InlineData("fingerprint")]
    [InlineData("account")]
    [InlineData("model")]
    [InlineData("route")]
    public async Task ChangeAfterRealWriterAcquisitionRefusesBeforeSendAndReleasesOwnership(string change)
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        f.Locks.AfterAcquire = async () => { await f.Change(change); };
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, result.Status); Assert.True(result.IsTerminal);
        Assert.Empty(f.Handler.Requests);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='MirasimTransportAuthorized'"));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("max")]
    [InlineData("disabled")]
    public async Task SessionContinuationRechecksCurrentPolicyAndDoesNotAcceptCallerReplacement(string change)
    {
        using var f = await Fixture.Create(); var binding = await f.CreateSession(); f.Handler.Requests.Clear();
        await f.Change(change);
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.Service.ContinueSessionAsync(binding));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("profile")]
    [InlineData("model")]
    [InlineData("harness")]
    public async Task CallerCannotReplaceTheLocallyRegisteredSessionBinding(string change)
    {
        using var f = await Fixture.Create(); var binding = await f.CreateSession(); f.Handler.Requests.Clear();
        var request = change switch
        {
            "session" => f.Request with { SessionKey = "foreign" }, "root" => f.Request with { CanonicalRootPath = "D:/foreign" },
            "project" => f.Request with { ProjectId = "foreign" }, "profile" => f.Request with { ProviderProfileId = "provider-1" },
            "model" => f.Request with { RequestedModelId = "foreign" }, _ => f.Request with { RequestedHarness = "foreign" }
        };
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(request)).Status);
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.Service.ContinueSessionAsync(binding with { WorkspacePath = "D:/foreign" }));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData("SessionCreate")]
    [InlineData("Turn")]
    public async Task AuditFailureRefusesHttpAndReleasesAcquiredWriter(string operation)
    {
        using var f = await Fixture.Create();
        if (operation == "Turn") await f.CreateSession();
        f.Handler.Requests.Clear();
        await f.Sql("CREATE TRIGGER refuse_mirasim_policy BEFORE INSERT ON ActivityEvents WHEN NEW.TitleRedacted='Mirasim: локальная policy проверена' BEGIN SELECT RAISE(ABORT,'synthetic audit refusal'); END;");
        if (operation == "Turn") Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        else await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.CreateSession());
        Assert.Empty(f.Handler.Requests);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task FailedLocalReleaseRetainsOwnershipAndReconciliationRetriesOnlyCleanup()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        f.Locks.AfterAcquire = async () => { await f.Change("class"); }; f.Locks.RefuseRelease = true;
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, result.Status); Assert.True(result.RequiresLocalCleanup); Assert.False(result.IsTerminal);
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.True((await f.Service.ExecuteTurnAsync(f.Request)).RequiresLocalCleanup); Assert.Empty(f.Handler.Requests);
        Assert.True((await f.Service.ReconcileTurnAsync(f.Request.SessionKey, "")).RequiresLocalCleanup);
        f.Locks.RefuseRelease = false;
        var cleared = await f.Service.ReconcileTurnAsync(f.Request.SessionKey, "");
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, cleared.Status); Assert.True(cleared.IsTerminal); Assert.False(cleared.RequiresLocalCleanup);
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task UnknownNativeSessionAndMissingContextCannotBecomePermission()
    {
        using var f = await Fixture.Create();
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.Service.CreateSessionAsync("instance", "codex", "model", f.Request.CanonicalRootPath));
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task SecondarySupervisorCannotCreateOrUseAWorkspaceSession()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        using var secondary = new ApplicationInstanceGuard(f.AppDataRoot);
        Assert.True(secondary.IsViewOnly);
        var service = new MirasimSessionLifecycleService(f.Client, Fixture.Options(), f.Locks,
            egressPolicy: new SqliteMirasimEgressPolicy(f.Factory, secondary, TimeProvider.System));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => service.CreateSessionAsync("instance", "codex", "model", f.Request.CanonicalRootPath, projectContext: f.Context));
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await service.ExecuteTurnAsync(f.Request)).Status);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task CancellationAfterWriterAcquireReleasesLocalOwnershipWithoutSend()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        using var cancel = new CancellationTokenSource();
        f.Locks.AfterAcquire = () => { cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExecuteTurnAsync(f.Request, cancel.Token));
        Assert.Empty(f.Handler.Requests);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task TerminalCommitFailureRetainsRealWriterAndReconcileDoesNotRepeatThePrompt()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        await f.Sql("CREATE TRIGGER refuse_mirasim_terminal BEFORE UPDATE OF State ON Executions WHEN NEW.State='Succeeded' BEGIN SELECT RAISE(ABORT,'synthetic terminal refusal'); END;");
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status); Assert.False(result.IsTerminal);
        Assert.Equal("Running", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(MirasimTurnStatus.Ambiguous, (await f.Service.ExecuteTurnAsync(f.Request with { ExecutionMode = null })).Status);
        Assert.Single(f.Handler.Requests);
        await f.Sql("DROP TRIGGER refuse_mirasim_terminal");
        f.Locks.BeforeRelease = async () => Assert.Equal("Succeeded", await f.Sql("SELECT State FROM Executions"));
        var reconciled = await f.Service.ReconcileTurnAsync(f.Request.SessionKey, result.TurnId);
        Assert.Equal(MirasimTurnStatus.Completed, reconciled.Status);
        Assert.Equal(2, f.Handler.Requests.Count); Assert.Single(f.Handler.Requests.Where(s => s.Contains(f.Request.Prompt, StringComparison.Ordinal)));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions WHERE ObservedRouteId IS NOT NULL"));
    }

    [Fact]
    public async Task TerminalReleaseFailureRemainsReconcilableAfterDurableSuccess()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear(); f.Locks.RefuseRelease = true;
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status); Assert.Equal("Succeeded", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        f.Locks.RefuseRelease = false;
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ReconcileTurnAsync(f.Request.SessionKey, result.TurnId)).Status);
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Single(f.Handler.Requests.Where(s => s.Contains(f.Request.Prompt, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LocalTerminalWriteFailureRetriesOnlyTheJournalAndOwnership()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        f.Locks.AfterAcquire = async () => { await f.Change("class"); };
        await f.Sql("CREATE TRIGGER refuse_mirasim_local BEFORE UPDATE OF State ON Executions WHEN NEW.State='Failed' BEGIN SELECT RAISE(ABORT,'synthetic local terminal refusal'); END;");
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.True(result.RequiresLocalCleanup); Assert.False(result.IsTerminal); Assert.Empty(f.Handler.Requests);
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await f.Sql("DROP TRIGGER refuse_mirasim_local");
        f.Locks.BeforeRelease = async () => Assert.Equal("Failed", await f.Sql("SELECT State FROM Executions"));
        Assert.True((await f.Service.ReconcileTurnAsync(f.Request.SessionKey, "")).IsTerminal);
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task AtomicAdmissionAuditFailureLeavesNoRowsOrWriter()
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        await f.Sql("CREATE TRIGGER refuse_mirasim_admission BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='MirasimAdmitted' BEGIN SELECT RAISE(ABORT,'synthetic admission refusal'); END;");
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        foreach (var table in new[] { "Sessions", "Executions", "ClientRequests", "ProjectLocks", "ExecutionEvents" })
            Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM " + table));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task MissingOrAmbiguousSavedRouteCannotSelectADefaultAccount(string change)
    {
        using var f = await Fixture.Create(); await f.CreateSession(); f.Handler.Requests.Clear();
        if (change == "missing") await f.Sql("UPDATE Routes SET IsEnabled=0");
        else await f.Sql("INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,ReasoningEffort,SpeedMode,ExecutionMode,MaxDataClass,IsEnabled,Health,ManualPriority,CreatedAtUtc,UpdatedAtUtc) SELECT 'route-2',Backend,ProviderProfileId,AccountId,ModelId,ReasoningEffort,SpeedMode,ExecutionMode,MaxDataClass,IsEnabled,Health,ManualPriority,CreatedAtUtc,UpdatedAtUtc FROM Routes WHERE Id='route-1'");
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
        if (change == "ambiguous")
        {
            Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request with { RequestedRouteId = "route-1" })).Status);
            Assert.Single(f.Handler.Requests);
        }
    }

    [Fact]
    public void ProductionCompositionRegistersRealPolicyJournalAndKeepsNativeStoreLazy()
    {
        using var directory = new TestDirectory();
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: directory.Root)
            .ConfigureServices((_, services) => services.AddMirasimBackend()).Build();
        Assert.IsType<SqliteMirasimEgressPolicy>(host.Services.GetRequiredService<IMirasimEgressPolicy>());
        Assert.IsType<SqliteMirasimExecutionJournal>(host.Services.GetRequiredService<IMirasimExecutionJournal>());
        Assert.IsType<MirasimSessionLifecycleService>(host.Services.GetRequiredService<IMirasimSessionLifecycleService>());
        Assert.False(File.Exists(Path.Combine(directory.Root, "llmgateway/accounts.json")));
        Assert.False(File.Exists(Path.Combine(directory.Root, "llmworkgui.db")));
    }

    [Fact]
    public async Task SameSessionCannotQueueAnotherTurnWhileItsWriterWaits()
    {
        using var f = await Fixture.Create(); var binding = await f.CreateSession(); f.Handler.Requests.Clear();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Locks.AfterAcquire = async () => { entered.TrySetResult(); await release.Task; };
        var first = f.Service.ExecuteTurnAsync(f.Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var refused = await f.Service.ExecuteTurnAsync(f.Request with { ExecutionId = "foreign-execution", ExecutionMode = "plan" });
            Assert.Equal(MirasimTurnStatus.Ambiguous, refused.Status); Assert.Empty(f.Handler.Requests);
            await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.Service.ContinueSessionAsync(binding));
            await Assert.ThrowsAsync<MirasimEgressPolicyException>(() => f.CreateSession());
            Assert.Empty(f.Handler.Requests);
            Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM Executions"));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(MirasimTurnStatus.Completed, (await first).Status); Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public async Task MissingWriterServiceAndExecutionReplayCannotTransmit()
    {
        using var f = await Fixture.Create();
        var noLocks = new MirasimSessionLifecycleService(f.Client, Fixture.Options(),
            egressPolicy: new SqliteMirasimEgressPolicy(f.Factory, f.Guard, TimeProvider.System),
            executionJournal: new SqliteMirasimExecutionJournal(f.Factory, f.Guard, TimeProvider.System));
        await noLocks.CreateSessionAsync("instance", "codex", "model", f.Request.CanonicalRootPath, projectContext: f.Context);
        f.Handler.Requests.Clear();
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await noLocks.ExecuteTurnAsync(f.Request)).Status);
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
        await f.CreateSession(); f.Handler.Requests.Clear();
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        Assert.Single(f.Handler.Requests); Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM Executions"));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabase _db = new();
        public ApplicationInstanceGuard Guard = null!;
        public Locks Locks = null!;
        public HttpClient Client = null!;
        public Handler Handler { get; } = new();
        public MirasimSessionLifecycleService Service = null!;
        public ProjectProviderContext Context { get; } = new("project-1", "mirasim");
        public MirasimTurnRequest Request = null!;
        public string AppDataRoot => _db.Root;
        public LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory Factory => _db.Factory;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                await f._db.InitializeAsync(); await f._db.SeedRouteChainAsync(providerProfileId: "mirasim"); Directory.CreateDirectory(f._db.GetWorkspacePath());
                await new SqliteProviderProfileRepository(f._db.Factory).UpsertAsync(new ProviderProfile("mirasim", "Synthetic local host",
                    BackendType.Mirasim, null, null, DataClassification.PrivateSource, true));
                await f.Sql("UPDATE Models SET Backend='Mirasim',ProviderModelId='model'; UPDATE Routes SET Backend='Mirasim'");
                f.Guard = new(f._db.Root);
                f.Locks = new(new CheckoutLockService(new SqliteProjectLockRepository(f._db.Factory), f.Guard, TimeProvider.System));
                f.Client = new(f.Handler) { Timeout = Timeout.InfiniteTimeSpan };
                f.Service = new(f.Client, Options(), f.Locks, egressPolicy: new SqliteMirasimEgressPolicy(f._db.Factory, f.Guard, TimeProvider.System),
                    executionJournal: new SqliteMirasimExecutionJournal(f._db.Factory, f.Guard, TimeProvider.System));
                f.Request = new() { ProjectId = "project-1", ProviderProfileId = "mirasim", CanonicalRootPath = f._db.GetWorkspacePath(),
                    SessionKey = "session-fixture", RequestedHarness = "codex", RequestedModelId = "model", ExecutionId = "mirasim-execution",
                    Prompt = "synthetic review text", ExecutionMode = "Write" };
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        public static IOptions<MirasimOptions> Options() => Microsoft.Extensions.Options.Options.Create(new MirasimOptions
            { Hostname = "127.0.0.1", Port = 4970, RequestTimeout = TimeSpan.FromSeconds(10) });
        public Task<MirasimSessionBinding> CreateSession() => Service.CreateSessionAsync("instance", "codex", "model", Request.CanonicalRootPath, projectContext: Context);
        public Task<object?> Change(string change) => Sql(change switch
        {
            "class" => "UPDATE Projects SET DataClassification='Restricted'",
            "max" => "UPDATE ProviderProfiles SET MaxDataClass='PublicSource' WHERE Id='mirasim'",
            "disabled" => "UPDATE ProviderProfiles SET IsEnabled=0 WHERE Id='mirasim'",
            "backend" => "UPDATE ProviderProfiles SET Backend='OpenCode' WHERE Id='mirasim'",
            "root" => "UPDATE Projects SET RootPath='D:/synthetic-other-root'",
            "fingerprint" => "UPDATE ProviderProfiles SET DisplayName='Changed while waiting' WHERE Id='mirasim'",
            "account" => "UPDATE Accounts SET AuthState='Unknown'",
            "model" => "UPDATE Models SET CapabilityState='Unknown'",
            "route" => "UPDATE Routes SET IsEnabled=0",
            _ => "DELETE FROM ProviderProfiles WHERE Id='mirasim'"
        });
        public async Task<object?> Sql(string sql)
        {
            await using var c = await _db.Factory.OpenConnectionAsync(); await using var command = c.CreateCommand(); command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        public void Dispose() { Client?.Dispose(); Locks?.Dispose(); Guard?.Dispose(); _db.Dispose(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];
        public bool KeepTurnRunning;
        public Func<Task<object?>>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Content is null ? "GET " + request.RequestUri!.AbsolutePath : await request.Content.ReadAsStringAsync(token));
            AuthorizationHeaders.Add(request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null);
            if (BeforeResponse is { } action) await action();
            var session = request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal)
                || request.RequestUri.AbsolutePath.EndsWith("/continue", StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = new StringContent(session
                ? "{\"sessionKey\":\"session-fixture\",\"routeMode\":\"manualOnly\"}"
                : KeepTurnRunning && request.RequestUri.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                    ? "{\"sessionKey\":\"session-fixture\",\"turnId\":\"turn-fixture\",\"phase\":\"running\",\"model\":\"model\"}"
                    : "{\"sessionKey\":\"session-fixture\",\"turnId\":\"turn-fixture\",\"phase\":\"done\",\"terminal\":true,\"model\":\"model\",\"response\":\"answer\"}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Locks(ICheckoutLockService inner) : ICheckoutLockService, IDisposable
    {
        private readonly List<ICheckoutLockToken> _owned = [];
        public Func<Task>? AfterAcquire;
        public bool RefuseRelease;
        public Func<Task>? BeforeRelease;
        public bool RequiresWriterLock(string? mode) => inner.RequiresWriterLock(mode);
        public bool RequiresWriterLock(WorkflowRole role, string? mode) => inner.RequiresWriterLock(role, mode);
        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string project, string root, string execution, long generation,
            string? mode, WorkflowRole role = WorkflowRole.Unknown, CancellationToken token = default) => inner.AcquireLockForExecutionAsync(project, root, execution, generation, mode, role, token);
        public async Task<ICheckoutLockToken> AcquireWriterLockAsync(string project, string root, string execution, long generation, CancellationToken token = default)
        {
            var owned = await inner.AcquireWriterLockAsync(project, root, execution, generation, token); _owned.Add(owned);
            if (AfterAcquire is { } action) await action(); return new Token(owned, this);
        }
        public void Dispose() { foreach (var item in _owned) item.Dispose(); }
        private sealed class Token(ICheckoutLockToken innerToken, Locks owner) : ICheckoutLockToken
        {
            public string LockId => innerToken.LockId; public string ProjectId => innerToken.ProjectId;
            public string CanonicalRootPath => innerToken.CanonicalRootPath; public string ExecutionId => innerToken.ExecutionId;
            public string ApplicationInstanceId => innerToken.ApplicationInstanceId; public bool IsHeld => innerToken.IsHeld;
            public async Task ReleaseAsync(string reason, CancellationToken token = default)
            {
                if (owner.BeforeRelease is { } check) await check();
                if (owner.RefuseRelease) throw new IOException("Synthetic local release failure");
                await innerToken.ReleaseAsync(reason, token);
            }
            public void Dispose() => innerToken.Dispose();
            public ValueTask DisposeAsync() => innerToken.DisposeAsync();
        }
    }
}
