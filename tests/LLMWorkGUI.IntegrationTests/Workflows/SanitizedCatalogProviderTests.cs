using System.Text.Json;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class SanitizedCatalogProviderTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private readonly SqliteProviderProfileRepository _providerRepository;
    private readonly SqliteAccountRepository _accountRepository;
    private readonly SqliteHealthStateRepository _healthStateRepository;
    private readonly SanitizedCatalogProvider _provider;

    public SanitizedCatalogProviderTests()
    {
        _providerRepository = new SqliteProviderProfileRepository(_database.Factory);
        _accountRepository = new SqliteAccountRepository(_database.Factory);
        _healthStateRepository = new SqliteHealthStateRepository(_database.Factory);
        _provider = new SanitizedCatalogProvider(
            _providerRepository,
            _accountRepository,
            _healthStateRepository,
            modelCatalogSource: null,
            new FixedTimeProvider(FixedNow));
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_ProjectsLocalStateWithoutSecrets()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-1", "OpenCode Local", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync("acct-r1-high", "prov-1", "DeepSeek R1 Account", reserveThreshold: 0.25);
        await SeedAccountHealthAsync("acct-r1-high", HealthState.Degraded);

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var provider = Assert.Single(catalog.Providers);
        Assert.Equal("prov-1", provider.ProviderId);
        Assert.Equal("OpenCode Local", provider.DisplayName);
        Assert.Equal(BackendType.OpenCode, provider.Backend);
        Assert.True(provider.IsEnabled);

        var model = Assert.Single(catalog.Models);
        Assert.Equal("acct-r1-high", model.ModelId);
        Assert.Equal("DeepSeek R1 Account", model.DisplayName);
        Assert.Equal(ModelCapabilityFlags.None, model.Capabilities);
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Empty(model.SupportedSpeedModes);
        Assert.Equal(HealthState.Degraded, model.Health);
        Assert.True(model.IsRoutable);
        Assert.Equal(FixedNow, catalog.GeneratedAtUtc);

        var serialized = JsonSerializer.Serialize(catalog);

        Assert.DoesNotContain("urn:llmworkgui:secret:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretReference", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKeySecretReference", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_RedactsSecretLikeProviderAndAccountText()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-secret", "Provider sk-abcdefgh12345678", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync(
            "acct-secret",
            "prov-secret",
            "Account urn:llmworkgui:secret:acct-secret");

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var provider = Assert.Single(catalog.Providers);
        Assert.DoesNotContain("sk-abcdefgh12345678", provider.DisplayName, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", provider.DisplayName, StringComparison.Ordinal);

        var model = Assert.Single(catalog.Models);
        Assert.DoesNotContain("urn:llmworkgui:secret:", model.DisplayName, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", model.DisplayName, StringComparison.Ordinal);

        var serialized = JsonSerializer.Serialize(catalog);

        Assert.DoesNotContain("sk-abcdefgh12345678", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("urn:llmworkgui:secret:", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_MarksModelsUnroutableForDisabledProvider()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-off", "Disabled Provider", BackendType.OpenCode, isEnabled: false);
        await SeedAccountAsync("acct-off", "prov-off", "Disabled Account");

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var model = Assert.Single(catalog.Models);
        Assert.False(model.IsRoutable);
        Assert.False(Assert.Single(catalog.Providers).IsEnabled);
    }

    [Theory]
    [InlineData(AuthState.Invalid, true, HealthState.Healthy)]
    [InlineData(AuthState.Valid, false, HealthState.Healthy)]
    [InlineData(AuthState.Valid, true, HealthState.QuarantinedAuto)]
    public async Task GetSanitizedCatalogAsync_MarksModelsUnroutableForIneligibleAccount(
        AuthState authState,
        bool isEnabled,
        HealthState health)
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-1", "Provider", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync(
            "acct-1",
            "prov-1",
            "Account",
            authState: authState,
            isEnabled: isEnabled,
            health: health);

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var model = Assert.Single(catalog.Models);
        Assert.False(model.IsRoutable);
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_FallsBackToAccountHealthWhenNoHealthRecord()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-1", "Provider", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync("acct-1", "prov-1", "Account", health: HealthState.QuarantinedAuto);

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var model = Assert.Single(catalog.Models);
        Assert.Equal(HealthState.QuarantinedAuto, model.Health);
        Assert.False(model.IsRoutable);
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_OrdersProvidersAndModelsDeterministically()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-zeta", "Zeta", BackendType.OpenCode, isEnabled: true);
        await SeedProviderAsync("prov-alpha", "Alpha", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync("acct-zeta", "prov-zeta", "Zeta Account");
        await SeedAccountAsync("acct-alpha", "prov-alpha", "Alpha Account");

        var catalog = await _provider.GetSanitizedCatalogAsync();

        Assert.Equal(new[] { "prov-alpha", "prov-zeta" }, catalog.Providers.Select(p => p.ProviderId));
        Assert.Equal(new[] { "acct-alpha", "acct-zeta" }, catalog.Models.Select(m => m.ModelId));
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_ReturnsEmptyCatalogForEmptyLocalState()
    {
        await _database.InitializeAsync();

        var catalog = await _provider.GetSanitizedCatalogAsync();

        Assert.Empty(catalog.Providers);
        Assert.Empty(catalog.Models);
        Assert.Equal(FixedNow, catalog.GeneratedAtUtc);
    }

    [Fact]
    public async Task GetSanitizedCatalogAsync_DoesNotInferCapabilitiesFromAccountIdentity()
    {
        await _database.InitializeAsync();
        await SeedProviderAsync("prov-1", "Provider", BackendType.OpenCode, isEnabled: true);
        await SeedAccountAsync("acct-gpt-4o-mini", "prov-1", "Fast Model");

        var catalog = await _provider.GetSanitizedCatalogAsync();

        var model = Assert.Single(catalog.Models);
        Assert.Empty(model.SupportedSpeedModes);
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Equal(ModelCapabilityFlags.None, model.Capabilities);
        Assert.Null(model.ContextWindow);
    }

    private async Task SeedProviderAsync(
        string id,
        string displayName,
        BackendType backend,
        bool isEnabled)
    {
        var profile = new ProviderProfile(
            id,
            displayName,
            backend,
            "http://127.0.0.1:11434/v1",
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled);

        await _providerRepository.UpsertAsync(
            profile,
            apiKeySecretReference: $"urn:llmworkgui:secret:{id}-api-key");
    }

    private async Task SeedAccountAsync(
        string id,
        string providerProfileId,
        string displayName,
        AuthState authState = AuthState.Valid,
        bool isEnabled = true,
        HealthState health = HealthState.Healthy,
        double? reserveThreshold = null)
    {
        var account = new Account(
            id,
            providerProfileId,
            displayName,
            providerNativeId: null,
            authState,
            manualPriority: 0,
            isEnabled,
            health,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold,
            sessionBindings: null,
            secretReference: $"urn:llmworkgui:secret:{id}-token");

        await _accountRepository.SaveAsync(account);
    }

    private async Task SeedAccountHealthAsync(string accountId, HealthState state)
    {
        await _healthStateRepository.UpsertAsync(new HealthStateRecord(
            $"health-{accountId}",
            "account",
            accountId,
            state,
            ErrorClass: null,
            FailureCount: 0,
            WindowStartedAt: null,
            CooldownUntil: null,
            EvidenceRedactedJson: null,
            UpdatedAt: FixedNow));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
