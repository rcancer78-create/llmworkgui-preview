namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Manages the lifecycle of loopback star-cliproxy instances through the shared process
/// supervisor (ТЗ §6.11a). When the gateway executable cannot be located the manager reports a
/// pure degraded mode with an exact blocker instead of falling back to OpenCode or a direct CLI.
/// </summary>
public interface IStarCliProxyServerManager
{
    bool IsAvailable { get; }

    string? AvailabilityBlocker { get; }

    Task<IStarCliProxyServerInstance> StartServerAsync(
        StarCliProxyServerStartRequest request,
        CancellationToken cancellationToken = default);

    Task StopServerAsync(
        IStarCliProxyServerInstance instance,
        CancellationToken cancellationToken = default);

    Task<StarCliProxyHealthStatus> CheckHealthAsync(
        IStarCliProxyServerInstance instance,
        CancellationToken cancellationToken = default);
}
