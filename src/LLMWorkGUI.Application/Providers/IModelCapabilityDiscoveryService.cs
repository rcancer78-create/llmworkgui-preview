namespace LLMWorkGUI.Application.Providers;

public interface IModelCapabilityDiscoveryService
{
    Task DiscoverAsync(string modelId, string accountId, CancellationToken cancellationToken = default);
}
