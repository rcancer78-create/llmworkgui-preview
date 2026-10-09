namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;

public sealed class RouteMismatchException : Exception
{
    public RouteMismatchException(RouteEvidence evidence)
        : base(BuildMessage(evidence))
    {
        RouteEvidence = evidence;
    }

    public RouteEvidence RouteEvidence { get; }

    private static string BuildMessage(RouteEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.ObservedBinding is null)
        {
            return "The observed OpenCode route is missing; requested route evidence must not be " +
                "substituted for observed route evidence.";
        }

        return evidence.Mismatches.Count == 0
            ? "The observed OpenCode route does not match the requested route."
            : "The observed OpenCode route does not match the requested route: " +
                string.Join("; ", evidence.Mismatches);
    }
}
