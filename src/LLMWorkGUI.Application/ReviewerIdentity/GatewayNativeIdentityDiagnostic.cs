namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// What the persisted library does and does not say about gateway-native identity, stated as counts and
/// named rows.
/// <para>
/// This is a completeness report about storage, and it is deliberately incapable of declaring a reviewer
/// turn available. <see cref="EnablesReviewerDispatch"/> is a constant false for the same reason
/// <c>ReviewChannelTurnReport.ObservedRouteId</c> is a constant null: a library in which every route carries
/// a gateway identity still has not produced a single response-origin observation, because nothing in this
/// build has yet received one from a gateway that reports it. Storing the identity is the precondition for
/// observing it, not the observation.
/// </para>
/// </summary>
public sealed record GatewayNativeIdentityDiagnostic
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private GatewayNativeIdentityDiagnostic(
        int routeRowCount,
        int joinedRouteCount,
        int boundRouteCount,
        int eligibleRouteCount,
        IReadOnlyList<string> unboundRouteIds,
        IReadOnlyList<string> incoherentRouteIds,
        IReadOnlyList<string> ambiguousTupleRouteIds)
    {
        RouteRowCount = routeRowCount;
        JoinedRouteCount = joinedRouteCount;
        BoundRouteCount = boundRouteCount;
        EligibleRouteCount = eligibleRouteCount;
        UnboundRouteIds = unboundRouteIds;
        IncoherentRouteIds = incoherentRouteIds;
        AmbiguousTupleRouteIds = ambiguousTupleRouteIds;
    }

    /// <summary>Every row in <c>Routes</c>, including rows the reader could not join to all three rows.</summary>
    public int RouteRowCount { get; }

    /// <summary>Rows whose provider profile, account and model all resolved to a stored row.</summary>
    public int JoinedRouteCount { get; }

    /// <summary>Joined rows that carry all three gateway-native names.</summary>
    public int BoundRouteCount { get; }

    /// <summary>Joined rows that are enabled, healthy, coherent and bound: the rows a response could resolve to.</summary>
    public int EligibleRouteCount { get; }

    public IReadOnlyList<string> UnboundRouteIds { get; }

    /// <summary>Rows whose account or model belongs to a different provider profile than the route names.</summary>
    public IReadOnlyList<string> IncoherentRouteIds { get; }

    /// <summary>
    /// Rows sharing a provider/account/model triple with at least one other row. Each such pair is separated
    /// only by its mode dimensions, and a native-name observation that does not report all three of those is
    /// refused outright rather than matched, so a tuple that is shared cannot be resolved by name at all until
    /// the gateway reports the full mode triple.
    /// </summary>
    public IReadOnlyList<string> AmbiguousTupleRouteIds { get; }

    /// <summary>
    /// Always false. See the type remarks: a fully bound library proves that a response-origin observation
    /// could be resolved if one arrived, and says nothing about whether one ever will.
    /// </summary>
    public bool EnablesReviewerDispatch => false;

    /// <summary>True only when every persisted route row is bound, coherent and unambiguous.</summary>
    public bool IsMappingComplete =>
        RouteRowCount > 0
        && RouteRowCount == BoundRouteCount
        && IncoherentRouteIds.Count == 0
        && UnboundRouteIds.Count == 0
        && AmbiguousTupleRouteIds.Count == 0;

    /// <summary>
    /// One sentence about storage. It never contains "review-ready", "ready to dispatch" or any wording a
    /// caller could read as a permission, and it never asserts that a gateway has been observed.
    /// </summary>
    public string Summary =>
        $"{BoundRouteCount} of {RouteRowCount} persisted routes carry a complete gateway-native identity, "
            + $"{UnboundRouteIds.Count} are unbound, {IncoherentRouteIds.Count} are internally incoherent and "
            + $"{AmbiguousTupleRouteIds.Count} share a provider/account/model triple with another route. "
            + "Storage completeness only: this build has still observed no gateway response-origin identity, so "
            + "the reviewer channel remains refused for every route.";

    /// <summary>
    /// Reports the library as it stands at <paramref name="now"/>. The eligibility figures are computed with
    /// the same rule the resolver uses, so a row the diagnostic calls eligible and a row the resolver would
    /// consider are the same row - a report that disagreed with the answer it is a precondition for would be
    /// worse than no report.
    /// </summary>
    public static GatewayNativeIdentityDiagnostic Report(
        GatewayRouteCandidateSnapshot snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidates = snapshot.Candidates;
        var unbound = new List<string>();
        var incoherent = new List<string>();
        var eligible = 0;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsInternallyCoherent)
            {
                incoherent.Add(candidate.Route.Id);
            }

            if (!candidate.HasEveryNativeName)
            {
                unbound.Add(candidate.Route.Id);
            }

            if (GatewayRouteIdentityResolver.IsEligible(candidate, now))
            {
                eligible++;
            }
        }

        var sharedTuples = candidates
            .GroupBy(candidate => candidate.NamespaceTuple)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(candidate => candidate.Route.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return new GatewayNativeIdentityDiagnostic(
            snapshot.RouteRowCount,
            candidates.Count,
            candidates.Count(candidate => candidate.HasEveryNativeName),
            eligible,
            Sort(unbound),
            Sort(incoherent),
            sharedTuples);
    }

    private static IReadOnlyList<string> Sort(List<string> ids) =>
        ids.Count == 0 ? None : ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}

/// <summary>
/// A read of the whole <c>Routes</c> table plus the rows each route names.
/// <para>
/// <see cref="RouteRowCount"/> is counted from <c>Routes</c> itself rather than derived from the join, so a
/// row whose provider profile, account or model is missing shows up as a shortfall. A report that counted
/// only what it could read would describe a smaller library than the operator has and imply the rest does
/// not exist.
/// </para>
/// </summary>
public sealed record GatewayRouteCandidateSnapshot(int RouteRowCount, IReadOnlyList<GatewayRouteCandidate> Candidates)
{
    private static readonly IReadOnlyList<GatewayRouteCandidate> None = Array.Empty<GatewayRouteCandidate>();

    public static GatewayRouteCandidateSnapshot Empty { get; } = new(0, None);
}
