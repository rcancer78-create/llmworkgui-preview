using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class ModelRefreshServiceTests
{
    [Fact]
    public async Task MissingCredential_RefusesRefreshInsteadOfReturningConfiguredModels()
    {
        var service = new ModelRefreshService(new ProviderConnectionTestService());
        var settings = new CustomProviderSettings("provider", "Provider", "http://127.0.0.1:59998/v1",
            apiKeySecretRef: "urn:llmworkgui:secret:missing",
            models: [new CustomProviderModelSettings("model", "Model", [])]);
        await Assert.ThrowsAsync<ProviderCredentialUnavailableException>(() => service.RefreshModelsAsync(settings));
    }

    [Fact]
    public async Task RefreshModelsAsync_AgainstLocalMockServer_LeavesUnreportedCapabilitiesUnknown()
    {
        await using var server = new LocalMockAiServer();
        server.Models.Clear();
        server.Models.Add(new DiscoveredModelInfo("o1-mini", "O1 Mini", "mock-provider"));
        server.Models.Add(new DiscoveredModelInfo("gpt-4o", "GPT-4o", "mock-provider"));
        await server.StartAsync();

        var connectionService = new ProviderConnectionTestService();
        var refreshService = new ModelRefreshService(connectionService);

        var settings = new CustomProviderSettings("local-mock", "Local Mock", server.BaseUrl);

        var models = await refreshService.RefreshModelsAsync(settings);

        Assert.NotNull(models);
        Assert.Equal(2, models.Count);

        var o1 = models.First(m => m.Id == "o1-mini");
        Assert.False(o1.SupportsReasoning);
        Assert.Empty(o1.SupportedReasoningEfforts);

        var gpt4o = models.First(m => m.Id == "gpt-4o");
        Assert.False(gpt4o.SupportsReasoning);
        Assert.False(gpt4o.SupportsVision);
        Assert.False(gpt4o.SupportsToolCalling);
        Assert.Null(gpt4o.ContextWindow);
    }

    [Fact]
    public async Task RefreshModelsAsync_WhenServerUnreachable_ReturnsFallbackConfiguredModels()
    {
        var connectionService = new ProviderConnectionTestService();
        var refreshService = new ModelRefreshService(connectionService);

        var preconfigured = new[]
        {
            new CustomProviderModelSettings("deepseek-r1", "DeepSeek R1", new[] { "low", "high" })
        };

        // Port 59998 is closed
        var settings = new CustomProviderSettings(
            "offline-provider",
            "Offline Provider",
            "http://127.0.0.1:59998/v1",
            models: preconfigured);

        var models = await refreshService.RefreshModelsAsync(settings);

        Assert.Single(models);
        Assert.Equal("deepseek-r1", models[0].Id);
        // Opaque configured variants are not a declaration of reasoning_effort support.
        Assert.False(models[0].SupportsReasoning);
        Assert.Empty(models[0].SupportedReasoningEfforts);
    }
}
