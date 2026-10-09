namespace LLMWorkGUI.Application.Providers;

/// <summary>A failed endpoint probe did not establish an inventory, including an empty one.</summary>
public sealed class ProviderModelDiscoveryUnavailableException : InvalidOperationException
{
    public ProviderConnectionStatus Status { get; }

    public ProviderModelDiscoveryUnavailableException(ProviderConnectionStatus status)
        : base("The provider model inventory could not be confirmed.") => Status = status;
}
