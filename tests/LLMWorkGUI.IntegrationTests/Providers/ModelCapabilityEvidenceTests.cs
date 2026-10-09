using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class ModelCapabilityEvidenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    private readonly FixedClock _clock = new(Now);
    private SqliteModelCapabilityEvidenceStore Store => new(_db.Factory, _guard!, new SensitiveDataFilter(), _clock);
    private SqliteModelRouteConfigurationService Configuration => new(_db.Factory, _guard!, new SensitiveDataFilter(), _clock);

    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private async Task SaveFresh(ModelCapabilityEvidence evidence) =>
        await Store.SaveAsync(evidence, await Store.CaptureContextAsync(evidence.ModelId, evidence.AccountId));
    private async Task SaveUserFresh(ModelCapabilityEvidence evidence, ModelCapabilityEvidence? expected) =>
        await Store.SaveUserDeclarationAsync(evidence, expected,
            await Store.CaptureContextAsync(evidence.ModelId, evidence.AccountId));

    private async Task Ready()
    {
        await _db.InitializeAsync();
        await _db.SeedRouteChainAsync();
        _guard = new ApplicationInstanceGuard(_db.Root);
        await Execute("UPDATE Routes SET ReasoningEffort='high',SpeedMode='fast',ExecutionMode='agent'");
    }
    private async Task Execute(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static ModelCapabilityEvidence Evidence() => new("model-1", "account-1", CapabilityState.Supported,
        ModelProvenance.ProviderReported, Now.AddMinutes(-1), Now.AddMinutes(5),
        ModelCapabilityFlags.Chat | ModelCapabilityFlags.ReasoningVariants, ["high", "max"], ["fast"], ["agent"], 128000);
    private static RouteSelectionRequest Request() => new()
    {
        Backend = BackendType.OpenCode, ProviderProfileId = "provider-1", ModelId = "model-1",
        ReasoningEffort = "high", SpeedMode = "fast", ExecutionMode = "agent"
    };
    private async Task<string?> Reject() => new RoutingModelDecision(await Configuration.ReadAsync(), Request(), _clock.GetUtcNow()).Reject("account-1");

    private SanitizedCatalogProvider Catalog() => new(
        new SqliteProviderProfileRepository(_db.Factory), new SqliteAccountRepository(_db.Factory),
        new SqliteHealthStateRepository(_db.Factory),
        new ApplicationSettingsProviderModelCatalog(new SqliteApplicationSettingsRepository(_db.Factory),
            new SensitiveDataFilter(), _db.Factory), _clock, configuration: Configuration);

    [Fact]
    public async Task WorkflowProjectionUsesConfirmedOptionsForTheExactNativeModel()
    {
        await Ready(); await SaveFresh(Evidence());
        var catalog = await Catalog().GetSanitizedCatalogAsync();
        var model = Assert.Single(catalog.Models);
        Assert.Equal("account-1", model.ModelId);
        var capability = model.CapabilitiesFor("model-1");
        Assert.Equal(Evidence().Flags, capability.Flags);
        Assert.Equal(new[] { "high", "max" }, capability.ReasoningEfforts);
        Assert.Equal(128000, capability.ContextLimit);
        Assert.Equal(ModelCapabilityFlags.None, model.CapabilitiesFor("unknown-model").Flags);

        _clock.Now = Evidence().ExpiresAtUtc;
        var expired = Assert.Single((await Catalog().GetSanitizedCatalogAsync()).Models);
        Assert.Equal(ModelCapabilityFlags.None, expired.Capabilities);
        Assert.Empty(expired.CapabilitiesFor("model-1").ReasoningEfforts);
    }

    [Fact]
    public async Task ModelsOnOneAccountDoNotBorrowEachOthersCapabilities()
    {
        await Ready(); await SaveFresh(Evidence());
        var second = await Configuration.SaveModelAsync(new("provider-1", "native-second", "Second",
            CapabilityState.Supported, true));
        await Configuration.SaveRouteAsync(new("provider-1", "account-1", second, null,
            DataClassification.PrivateSource, true, 1));
        await SaveFresh(Evidence() with
        {
            ModelId = second, Flags = ModelCapabilityFlags.Chat,
            ReasoningEfforts = [], SpeedModes = ["slow"], ContextLimit = 4096
        });
        var model = Assert.Single((await Catalog().GetSanitizedCatalogAsync()).Models);
        Assert.Equal(new[] { "high", "max" }, model.CapabilitiesFor("model-1").ReasoningEfforts);
        Assert.Empty(model.CapabilitiesFor("native-second").ReasoningEfforts);
        Assert.Equal(new[] { "slow" }, model.CapabilitiesFor("native-second").SpeedModes);
        Assert.Equal(ModelCapabilityFlags.Chat, model.Capabilities);
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Empty(model.SupportedSpeedModes);
        Assert.Null(model.ContextWindow);
    }

    [Fact]
    public async Task DisabledBindingCannotContributeWorkflowCapabilities()
    {
        await Ready(); await SaveFresh(Evidence());
        await Execute("UPDATE Routes SET IsEnabled=0");
        var model = Assert.Single((await Catalog().GetSanitizedCatalogAsync()).Models);
        Assert.Equal(ModelCapabilityFlags.None, model.CapabilitiesFor("model-1").Flags);
    }

    [Fact]
    public async Task SavedRouteOptionsRequireFreshScopedEvidenceAfterReopen()
    {
        await Ready();
        Assert.Contains("no current capability", await Reject());
        await SaveFresh(Evidence());
        var snapshot = await Configuration.ReadAsync();
        var item = Assert.Single(snapshot.Capabilities);
        Assert.Equal(ModelProvenance.ProviderReported, item.Provenance);
        Assert.Equal(128000, item.ContextLimit);
        Assert.Equal(new[] { "high", "max" }, item.ReasoningEfforts);
        Assert.Null(await Reject());
        _clock.Now = Evidence().ExpiresAtUtc;
        Assert.Contains("no current capability", await Reject());
    }

    [Theory]
    [InlineData(CapabilityState.Unknown)]
    [InlineData(CapabilityState.Unsupported)]
    [InlineData(CapabilityState.Stale)]
    [InlineData(CapabilityState.Error)]
    public async Task NonSupportedEvidenceCannotAuthorizeOptions(CapabilityState state)
    {
        await Ready(); await SaveFresh(Evidence() with { State = state });
        Assert.NotNull(await Reject());
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("speed")]
    [InlineData("mode")]
    public async Task EachRequestedOptionNeedsAnExactSupportedValue(string option)
    {
        await Ready();
        var item = Evidence();
        item = option switch
        {
            "reasoning" => item with { ReasoningEfforts = ["HIGH"] },
            "speed" => item with { SpeedModes = ["slow"] },
            _ => item with { ExecutionModes = ["ask"] }
        };
        await SaveFresh(item);
        Assert.NotNull(await Reject());
    }

    [Fact]
    public async Task EvidenceCannotCrossAccountOrProviderBoundaries()
    {
        await Ready();
        await Execute("""
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'other-account',ProviderProfileId,'Other',AuthState,Health,CreatedAtUtc,UpdatedAtUtc FROM Accounts WHERE Id='account-1'
            """);
        await SaveFresh(Evidence() with { AccountId = "other-account" });
        Assert.NotNull(await Reject());
        await _db.SeedRouteChainAsync(projectId: "project-2", projectRootPath: Path.Combine(_db.Root, "other-workspace"),
            providerProfileId: "provider-2", accountId: "account-2", modelId: "model-2", routeId: "route-2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveFresh(Evidence() with { AccountId = "account-2" }));
        Assert.Single((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task CursorOptionsRequireDiscoveryProvenance()
    {
        await Ready();
        await Execute("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp'; UPDATE Routes SET Backend='CursorAcp'");
        await SaveFresh(Evidence() with { Provenance = ModelProvenance.UserDefined });
        var decision = new RoutingModelDecision(await Configuration.ReadAsync(), Request() with { Backend = BackendType.CursorAcp }, Now);
        Assert.NotNull(decision.Reject("account-1"));
        await SaveFresh(Evidence() with { ObservedAtUtc = Now });
        decision = new RoutingModelDecision(await Configuration.ReadAsync(), Request() with { Backend = BackendType.CursorAcp }, Now);
        Assert.Null(decision.Reject("account-1"));
    }

    [Fact]
    public async Task OldOrConflictingReportsCannotOverwriteNewerState()
    {
        await Ready(); await SaveFresh(Evidence());
        await SaveFresh(Evidence()); // Idempotent replay.
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveFresh(Evidence() with { ObservedAtUtc = Now.AddMinutes(-2) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveFresh(Evidence() with { State = CapabilityState.Error }));
        await SaveFresh(Evidence() with { State = CapabilityState.Error, ObservedAtUtc = Now });
        Assert.NotNull(await Reject());
    }

    [Theory]
    [InlineData("UPDATE ModelCapabilities SET CapabilityValue='not-json'")]
    [InlineData("UPDATE ModelCapabilities SET CapabilityValue='{}'")]
    [InlineData("UPDATE ModelCapabilities SET State='Unknown'")]
    [InlineData("UPDATE ModelCapabilities SET Provenance='UserDefined'")]
    [InlineData("UPDATE ModelCapabilities SET UpdatedAtUtc='invalid'")]
    [InlineData("UPDATE ModelCapabilities SET CapabilityKey='legacy-high'")]
    public async Task MalformedOrInconsistentStoredEvidenceGrantsNothing(string sql)
    {
        await Ready(); await SaveFresh(Evidence()); await Execute(sql);
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
        Assert.NotNull(await Reject());
    }

    [Fact]
    public async Task UserDeclarationAssignsItsOwnProvenanceAndObservationTime()
    {
        await Ready();
        await SaveUserFresh(Evidence(), null);
        var saved = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        Assert.Equal(ModelProvenance.UserDefined, saved.Provenance);
        Assert.Equal(Now, saved.ObservedAtUtc);
        Assert.Null(await Reject());
        _clock.Now = Now.AddSeconds(1);
        await SaveUserFresh(saved with { State = CapabilityState.Unknown }, saved);
        Assert.NotNull(await Reject());
    }

    [Fact]
    public async Task StaleEditorCannotOverwriteDiscoveryOrAConcurrentCreation()
    {
        await Ready(); await SaveFresh(Evidence());
        var expected = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        _clock.Now = Now.AddSeconds(1);
        await SaveFresh(Evidence() with { ObservedAtUtc = Now, State = CapabilityState.Error });
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), expected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), null));
        Assert.Equal(CapabilityState.Error, Assert.Single((await Configuration.ReadAsync()).Capabilities).State);
    }

    [Fact]
    public async Task EditorCannotOverwriteChangedRowMetadataOrRecreateDeletedEvidence()
    {
        await Ready(); await SaveFresh(Evidence());
        var expected = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        await Execute("UPDATE ModelCapabilities SET State='Error'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), expected));
        await Execute("DELETE FROM ModelCapabilities");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), expected));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task CursorManualDeclarationIsRejectedByStorageAsWellAsTheUi()
    {
        await Ready();
        await Execute("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp'; UPDATE Routes SET Backend='CursorAcp'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), null));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task SecondaryInstanceCannotWriteCapabilityEvidence()
    {
        await Ready();
        using var secondary = new ApplicationInstanceGuard(_db.Root);
        var store = new SqliteModelCapabilityEvidenceStore(_db.Factory, secondary, new SensitiveDataFilter(), _clock);
        await Assert.ThrowsAsync<LLMWorkGUI.Application.Concurrency.SecondaryInstanceReadOnlyException>(async () => await store.SaveAsync(Evidence(), await Store.CaptureContextAsync("model-1", "account-1")));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task ClockRollbackDoesNotMakeFutureEvidenceUsable()
    {
        await Ready(); await SaveFresh(Evidence());
        _clock.Now = Now.AddMinutes(-2);
        Assert.NotNull(await Reject());
        var model = Assert.Single((await Catalog().GetSanitizedCatalogAsync()).Models);
        Assert.Equal(ModelCapabilityFlags.None, model.Capabilities);
    }

    [Fact]
    public async Task MovingAccountToAnotherProviderInvalidatesStoredScope()
    {
        await Ready(); await SaveFresh(Evidence());
        await Execute("""
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'other-provider','Other',Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc
            FROM ProviderProfiles WHERE Id='provider-1';
            UPDATE Accounts SET ProviderProfileId='other-provider' WHERE Id='account-1';
            """);
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
        Assert.NotNull(await Reject());
    }

    [Fact]
    public async Task InvalidOrFutureEvidenceIsRejectedBeforePersistence()
    {
        await Ready();
        foreach (var item in new[]
        {
            Evidence() with { ObservedAtUtc = Now.AddMinutes(1) },
            Evidence() with { ExpiresAtUtc = Now.AddDays(-1) },
            Evidence() with { ReasoningEfforts = null! },
            Evidence() with { ReasoningEfforts = ["high", "high"] },
            Evidence() with { Flags = (ModelCapabilityFlags)128 },
            Evidence() with { ContextLimit = -1 },
            Evidence() with { SpeedModes = [" fast"] }
        }) await Assert.ThrowsAsync<ArgumentException>(() => SaveFresh(item));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
