using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

/// <summary>
/// The local store of the backend model ids a provider profile is configured with. It is the only
/// source of a real OpenCode model id for an account whose record carries none, so it must round-trip a
/// confirmed <see cref="CustomProviderModelSettings"/> list, keep the profile/backend relation, and
/// refuse anything that is not a model id.
/// </summary>
public sealed class ProviderModelCatalogTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly ApplicationSettingsProviderModelCatalog _catalog;

    public ProviderModelCatalogTests()
    {
        _catalog = new ApplicationSettingsProviderModelCatalog(
            new SqliteApplicationSettingsRepository(_database.Factory),
            new SensitiveDataFilter());
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task SaveModelsAsync_RoundTripsTheConfirmedModelList()
    {
        await _database.InitializeAsync();

        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        await _catalog.SaveModelsAsync(query, new[]
        {
            new ProviderModelDescriptor("opencode/space-bunny-free", "Space Bunny Free"),
            new ProviderModelDescriptor("opencode/space-bunny-pro", "Space Bunny Pro")
        });

        var models = await _catalog.ListModelsAsync(query);

        Assert.Equal(
            new[] { "opencode/space-bunny-free", "opencode/space-bunny-pro" },
            models.Select(model => model.ModelId).ToArray());
        Assert.Equal("Space Bunny Free", models[0].DisplayName);
    }

    [Fact]
    public async Task ListModelsAsync_IsKeyedByProfileAndBackend()
    {
        await _database.InitializeAsync();

        await _catalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });

        Assert.Empty(await _catalog.ListModelsAsync(new ProviderModelCatalogQuery("prov-other", BackendType.OpenCode)));
        Assert.Empty(await _catalog.ListModelsAsync(new ProviderModelCatalogQuery("prov-oc", BackendType.CursorAcp)));
    }

    [Theory]
    [InlineData(@"C:\Users\tester\.codex\home")]
    [InlineData("/etc/opencode/models.json")]
    [InlineData("urn:llmworkgui:secret:provider-key")]
    [InlineData("   ")]
    public async Task SaveModelsAsync_DropsValuesThatAreNotModelIds(string value)
    {
        await _database.InitializeAsync();

        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        // A value the policy refuses never reaches the descriptor constructor, so it is simply not saved.
        await _catalog.SaveModelsAsync(
            query,
            ModelIdsOf(value).Select(modelId => new ProviderModelDescriptor(modelId)).ToArray());

        Assert.Empty(await _catalog.ListModelsAsync(query));
    }

    [Fact]
    public async Task SaveModelsAsync_RedactsASecretShapedModelId()
    {
        await _database.InitializeAsync();

        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        await _catalog.SaveModelsAsync(query, new[]
        {
            new ProviderModelDescriptor("sk-abcdefgh12345678", "api_key = sk-abcdefgh12345678")
        });

        var models = await _catalog.ListModelsAsync(query);

        Assert.Empty(models);
    }

    [Fact]
    public async Task SaveModelsAsync_DeduplicatesAndKeepsConfigurationOrder()
    {
        await _database.InitializeAsync();

        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        await _catalog.SaveModelsAsync(query, new[]
        {
            new ProviderModelDescriptor("opencode/b-model"),
            new ProviderModelDescriptor("opencode/a-model"),
            new ProviderModelDescriptor("opencode/b-model")
        });

        Assert.Equal(
            new[] { "opencode/b-model", "opencode/a-model" },
            (await _catalog.ListModelsAsync(query)).Select(model => model.ModelId).ToArray());
    }

    [Fact]
    public async Task SaveModelsAsync_WithNoUsableEntry_ClearsTheStoredList()
    {
        await _database.InitializeAsync();

        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        await _catalog.SaveModelsAsync(query, new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });
        await _catalog.ClearModelsAsync(query);

        Assert.Empty(await _catalog.ListModelsAsync(query));
    }

    [Fact]
    public async Task ListModelsAsync_TreatsCorruptStoredContentAsNoModels()
    {
        await _database.InitializeAsync();

        var settings = new SqliteApplicationSettingsRepository(_database.Factory);
        await settings.SetValueAsync(
            "provider.models.OpenCode.prov-oc",
            "{not json",
            "Json");

        Assert.Empty(await _catalog.ListModelsAsync(new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode)));
    }

    private static IReadOnlyList<string> ModelIdsOf(string value) =>
        string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };

    /// <summary>
    /// The production composition must actually wire the local store into the sanitized catalog, or the
    /// picker stays empty for every real OpenCode account.
    /// </summary>
    [Fact]
    public async Task ProductionComposition_FeedsTheLocalModelStoreIntoTheSanitizedCatalog()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        var profileRepository = provider.GetRequiredService<IProviderProfileRepository>();
        var accountRepository = provider.GetRequiredService<IAccountRepository>();

        await profileRepository.UpsertAsync(new ProviderProfile(
            "prov-oc",
            "OpenCode (local)",
            BackendType.OpenCode,
            baseUrl: null,
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true));

        // The production normal OpenCode account shape: no ProviderNativeId at all.
        await accountRepository.SaveAsync(new Account(
            "acct-oc",
            "prov-oc",
            "Default Account",
            providerNativeId: null,
            AuthState.Valid,
            manualPriority: 0,
            isEnabled: true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold: 0.25,
            sessionBindings: null,
            secretReference: null));

        var source = provider.GetRequiredService<IProviderModelCatalogSource>();
        var writer = provider.GetRequiredService<IProviderModelCatalogWriter>();

        // One local store serves both roles, so what the provider screen records is what the route sees.
        Assert.Same(source, writer);

        var catalogProvider = provider.GetRequiredService<ISanitizedCatalogProvider>();
        var query = new ProviderModelCatalogQuery("prov-oc", BackendType.OpenCode);

        Assert.Empty(Assert.Single((await catalogProvider.GetSanitizedCatalogAsync()).Models).SelectableBackendModelIds);

        await writer.SaveModelsAsync(query, new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });

        var row = Assert.Single((await catalogProvider.GetSanitizedCatalogAsync()).Models);
        Assert.Equal(new[] { "opencode/space-bunny-free" }, row.SelectableBackendModelIds);
        Assert.True(AdaptationRouteIdentity.TryCreate(row, out var identity));
        Assert.Equal("acct-oc", identity.AccountId);
        Assert.Equal("prov-oc", identity.ProviderProfileId);
        Assert.Equal(BackendType.OpenCode, identity.Backend);
        Assert.True(identity.IsSelectableForAdaptation);
    }
}
