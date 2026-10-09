using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;

public interface IRouteVerificationService
{
    RouteEvidence VerifyRoute(
        SessionBinding requested,
        SessionBinding? observed,
        string? evidenceSource = null);

    void EnsureRouteMatches(
        SessionBinding requested,
        SessionBinding? observed,
        string? evidenceSource = null);
}
