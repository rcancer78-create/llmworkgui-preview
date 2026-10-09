namespace LLMWorkGUI.Application.Providers;

/// <summary>A local credential gate refused refresh before an HTTP request could be sent.</summary>
public sealed class ProviderCredentialUnavailableException : InvalidOperationException
{
    public ProviderCredentialUnavailableException()
        : base("The provider credential is unavailable or is not bound to this provider.") { }
}
