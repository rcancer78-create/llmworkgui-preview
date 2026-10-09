namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// Why an observation could not be turned into exactly one persisted <c>Routes.Id</c>. Each value is a
/// distinct fact about what was missing, because the work that would fix each one is different.
/// </summary>
public enum GatewayRouteRefusalKind
{
    /// <summary>The observation resolved. Not a refusal.</summary>
    None = 0,

    /// <summary>
    /// The response carried no identity in either accepted form: not all three native names, and no gateway
    /// route key. A partial set of names is not a shorter identity, it is a name that matches more rows.
    /// </summary>
    NoNativeIdentityObserved,

    /// <summary>
    /// Two synthetic chunks of the same response reported different values for the same field. A stream
    /// that contradicts itself about which provider, account, model or mode answered has not identified
    /// anything, and this build will not pick a winner.
    /// </summary>
    ConflictingObservationChunks,

    /// <summary>
    /// The observed names are complete and the observation is in the route-key form, but nothing matches: the
    /// operator's library holds rows that agree on everything they do declare and record no gateway-native
    /// name. In other words the right rows are there with their gateway identity still unbound, which is a
    /// different answer from "no such route exists" and the one worth acting on.
    /// </summary>
    IncompletePersistedIdentity,

    /// <summary>No eligible row agrees with the observation.</summary>
    NoEligibleRoute,

    /// <summary>
    /// The observation is in the native-name form and did not report all three of
    /// <c>ReasoningEffort</c>, <c>SpeedMode</c> and <c>ExecutionMode</c>. The refusal is raised before a
    /// single row is read, because the missing dimension is a fact about the response and not about the
    /// library: the resolver cannot know whether the routes that share this triple differ only in the
    /// dimension the gateway was silent about, and it will not resolve on the assumption that they do not.
    /// </summary>
    UnobservedModeDimension,

    /// <summary>
    /// The observed names match more than one provider/account/model triple. The same gateway name would
    /// describe two different stored things, so the observation cannot be attributed to either. Uniqueness
    /// inside one namespace does not prevent this across namespaces.
    /// </summary>
    AmbiguousAcrossNamespaces,

    /// <summary>
    /// One triple, several routes, and every dimension this build can observe agrees - so the rows are
    /// indistinguishable by any value the build holds. Two persisted routes presenting an identical
    /// identity and mode tuple is a storage-level contradiction that the read cannot resolve.
    /// </summary>
    AmbiguousAcrossRoutes
}

/// <summary>
/// What the diagnostic resolver did with one synthetic response-origin observation.
/// <para>
/// <see cref="RouteId"/> is ephemeral. It exists to be printed, compared in a test and re-resolved against
/// the assignment that a future turn would actually hold; it is not to be written to
/// <c>Executions.ObservedRouteId</c>, to a reviewer execution binding, or to anything else that a gate
/// reads. Nothing in this build stores it, and a caller that wants to store a route it observed has to
/// obtain the observation from a backend again.
/// </para>
/// </summary>
public sealed record GatewayRouteResolution
{
    private static readonly IReadOnlyList<string> NoCandidates = Array.Empty<string>();

    private GatewayRouteResolution(
        GatewayRouteRefusalKind refusal,
        string? routeId,
        string reason,
        IReadOnlyList<string> candidateRouteIds,
        IReadOnlyList<string> unboundRouteIds)
    {
        Refusal = refusal;
        RouteId = routeId;
        Reason = reason;
        CandidateRouteIds = candidateRouteIds;
        UnboundRouteIds = unboundRouteIds;
    }

    /// <summary><see cref="GatewayRouteRefusalKind.None"/> only when a single route was identified.</summary>
    public GatewayRouteRefusalKind Refusal { get; }

    /// <summary>
    /// The single persisted <c>Routes.Id</c> the observation identified, or null on any refusal. EPHEMERAL:
    /// this value is a diagnostic answer, never provenance.
    /// </summary>
    public string? RouteId { get; }

    /// <summary>One sentence naming the refusal or the identification. Carries no payload from the response.</summary>
    public string Reason { get; }

    /// <summary>
    /// The persisted route ids that were eligible and agreed on every dimension both sides declared, sorted
    /// so the answer is the same on every call. More than one entry is the ambiguity, stated as data rather
    /// than as a claim about which of them ran.
    /// </summary>
    public IReadOnlyList<string> CandidateRouteIds { get; }

    /// <summary>
    /// Rows that agree on what they declare and are missing a gateway-native name, sorted. Present on an
    /// <see cref="GatewayRouteRefusalKind.IncompletePersistedIdentity"/> refusal so the operator can see
    /// which rows to bind.
    /// </summary>
    public IReadOnlyList<string> UnboundRouteIds { get; }

    public bool IsResolved => Refusal == GatewayRouteRefusalKind.None && RouteId is not null;

    public static GatewayRouteResolution Resolved(string routeId, string reason, IReadOnlyList<string> candidateRouteIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(candidateRouteIds);

        return new GatewayRouteResolution(
            GatewayRouteRefusalKind.None,
            routeId,
            reason,
            Normalize(candidateRouteIds),
            NoCandidates);
    }

    public static GatewayRouteResolution Refused(
        GatewayRouteRefusalKind refusal,
        string reason,
        IReadOnlyList<string>? candidateRouteIds = null,
        IReadOnlyList<string>? unboundRouteIds = null)
    {
        if (refusal == GatewayRouteRefusalKind.None)
        {
            throw new ArgumentException("A resolution without an identified route is a refusal.", nameof(refusal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new GatewayRouteResolution(
            refusal,
            routeId: null,
            reason,
            candidateRouteIds is null ? NoCandidates : Normalize(candidateRouteIds),
            unboundRouteIds is null ? NoCandidates : Normalize(unboundRouteIds));
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> routeIds) =>
        routeIds.Count == 0 ? NoCandidates : routeIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}
