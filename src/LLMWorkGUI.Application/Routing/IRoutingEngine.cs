namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Routing Engine service interface: selects an account and route for a GUI-managed session (ТЗ §6.5, ADR-0004 §6).
/// </summary>
public interface IRoutingEngine
{
    Task<RoutingDecision> SelectRouteAsync(
        RouteSelectionRequest request,
        CancellationToken cancellationToken = default);
}
