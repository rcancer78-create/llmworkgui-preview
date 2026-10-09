namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public interface IOpenCodeDiscoveryService
{
    Task<OpenCodeDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}
