namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public interface IMirasimClient
{
    Uri BaseUrl { get; }

    Task<MirasimHealthStatus> ProbeHealthAsync(CancellationToken cancellationToken = default);

    Task<bool> ProbeAuthenticatedEndpointAsync(
        string? testToken = null,
        CancellationToken cancellationToken = default);
}
