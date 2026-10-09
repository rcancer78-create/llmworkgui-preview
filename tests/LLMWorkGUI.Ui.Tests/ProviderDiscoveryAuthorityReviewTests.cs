using System.IO;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ProviderDiscoveryAuthorityReviewTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("endpoint")]
    public async Task DiscoveredModelsAreNotPersistedForChangedRoutingConfiguration(string changedField)
    {
        var world = new OwnedWorld();
        await world.Model.ExecuteRefreshModelsAsync();
        Assert.Equal("owned-model-a", Assert.Single(world.Model.DiscoveredModels).Id);
        if (changedField == "provider") world.Model.EditingProviderId = "owned-provider-b";
        else world.Model.EditingBaseUrl = "https://owned-b.invalid";

        await world.Model.ExecuteConfirmAndSaveAsync();

        var persisted = await world.Profiles.GetByIdAsync(world.Model.EditingProviderId);
        Assert.NotNull(persisted); // Do not accept an unrelated failed profile save as a catalog refusal.
        Assert.Equal(world.Model.EditingBaseUrl, persisted!.BaseUrl);
        Assert.DoesNotContain(world.Catalog.For(world.Model.EditingProviderId), model => model.ModelId == "owned-model-a");
    }

    [Fact]
    public async Task LateDiscoveryCannotPublishAgainstTheDifferentEndpointNowInTheForm()
    {
        var world = new OwnedWorld();
        var held = new TaskCompletionSource<IReadOnlyList<DiscoveredModelDetails>>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Discovery.Reply = () => held.Task;
        var pending = world.Model.ExecuteRefreshModelsAsync();
        try
        {
            Assert.Equal("https://owned-a.invalid", Assert.Single(world.Discovery.Settings).BaseUrl);
            Assert.True(world.Model.IsBusy);
            world.Model.EditingBaseUrl = "https://owned-b.invalid";
            held.SetResult([new DiscoveredModelDetails("owned-model-a", "Owned A")]);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(world.Model.DiscoveredModels);
            await world.Model.ExecuteConfirmAndSaveAsync();
            Assert.NotNull(await world.Profiles.GetByIdAsync("owned-provider-a"));
            Assert.Empty(world.Catalog.For("owned-provider-a"));
        }
        finally
        {
            held.TrySetResult([]);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedDiscoveryAfterEndpointChangeDoesNotReuseTheOldModelsAtSave()
    {
        var world = new OwnedWorld();
        await world.Model.ExecuteRefreshModelsAsync();
        world.Model.EditingBaseUrl = "https://owned-b.invalid";
        world.Discovery.Reply = () => Task.FromException<IReadOnlyList<DiscoveredModelDetails>>(new IOException("owned discovery failure"));
        await world.Model.ExecuteRefreshModelsAsync();

        await world.Model.ExecuteConfirmAndSaveAsync();

        var profile = await world.Profiles.GetByIdAsync("owned-provider-a");
        Assert.NotNull(profile);
        Assert.Equal("https://owned-b.invalid", profile!.BaseUrl);
        Assert.Empty(world.Catalog.For("owned-provider-a"));
    }

    [Fact]
    public async Task ConfirmedEmptyDiscoveryReplacesThePreviouslyConfiguredCatalog()
    {
        var world = new OwnedWorld();
        await world.Model.ExecuteRefreshModelsAsync();
        await world.Model.ExecuteConfirmAndSaveAsync();
        Assert.Equal("owned-model-a", Assert.Single(world.Catalog.For("owned-provider-a")).ModelId);
        world.Discovery.Reply = () => Task.FromResult<IReadOnlyList<DiscoveredModelDetails>>([]);

        await world.Model.ExecuteRefreshModelsAsync();
        Assert.Empty(world.Model.DiscoveredModels);
        await world.Model.ExecuteConfirmAndSaveAsync();

        Assert.Empty(world.Catalog.For("owned-provider-a"));
    }

    [Fact]
    public async Task CurrentDiscoveryRemainsUsableWhenOnlyTheDisplayNameChanges()
    {
        var world = new OwnedWorld();
        await world.Model.ExecuteRefreshModelsAsync();
        world.Model.EditingDisplayName = "Owned renamed provider";

        await world.Model.ExecuteConfirmAndSaveAsync();

        var profile = await world.Profiles.GetByIdAsync("owned-provider-a");
        Assert.NotNull(profile);
        Assert.Equal("Owned renamed provider", profile!.DisplayName);
        Assert.Equal("owned-model-a", Assert.Single(world.Catalog.For("owned-provider-a")).ModelId);
    }

    private sealed class OwnedWorld
    {
        public InMemoryProviderProfileRepository Profiles { get; } = new();
        public OwnedDiscovery Discovery { get; } = new();
        public OwnedCatalog Catalog { get; } = new();
        public ProvidersAccountsViewModel Model { get; }
        public OwnedWorld()
        {
            Model = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), new FakeTimeProvider()),
                profileRepository: Profiles, modelRefreshService: Discovery, modelCatalogWriter: Catalog);
            Model.EditingProviderId = "owned-provider-a";
            Model.EditingDisplayName = "Owned provider A";
            Model.EditingBaseUrl = "https://owned-a.invalid";
        }
    }

    private sealed class OwnedDiscovery : IModelRefreshService
    {
        public Func<Task<IReadOnlyList<DiscoveredModelDetails>>> Reply { get; set; } =
            () => Task.FromResult<IReadOnlyList<DiscoveredModelDetails>>([new DiscoveredModelDetails("owned-model-a", "Owned A")]);
        public List<CustomProviderSettings> Settings { get; } = new();
        public Task<IReadOnlyList<DiscoveredModelDetails>> RefreshModelsAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, CancellationToken cancellationToken = default)
        {
            Settings.Add(settings);
            return Reply();
        }
    }

    private sealed class OwnedCatalog : IProviderModelCatalogWriter
    {
        private readonly Dictionary<string, IReadOnlyList<ProviderModelDescriptor>> _models = new(StringComparer.Ordinal);
        public IReadOnlyList<ProviderModelDescriptor> For(string id) => _models.GetValueOrDefault(id) ?? [];
        public Task SaveModelsAsync(ProviderModelCatalogQuery query, IReadOnlyList<ProviderModelDescriptor> models,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(BackendType.OpenCode, query.Backend);
            _models[query.ProviderProfileId] = models.ToArray();
            return Task.CompletedTask;
        }
        public Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default)
        {
            _models.Remove(query.ProviderProfileId);
            return Task.CompletedTask;
        }
    }
}
