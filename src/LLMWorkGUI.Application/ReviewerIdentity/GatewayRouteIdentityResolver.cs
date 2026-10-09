using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// The deterministic, strict resolver for one response-origin observation, and nothing else.
/// <para><b>Why this exists and why it is not on any dispatch path.</b>
///
/// The read-only reviewer channel refuses every route because nothing in this build could turn the native
/// provider, account, model and mode a gateway reports into one persisted <c>Routes.Id</c>. Migration 012
/// gives those four facts a place to be stored, and this resolver is the read-time half of that: given a
/// synthetic observation and the persisted library, it either names exactly one route or it names a refusal
/// that says which fact was missing. It is not a dispatch decision and the composition root does not
/// register it, so no channel, executor or run service can obtain a route id from it.
/// </para>
/// <para><b>The rule, stated once.</b>
///
/// A candidate is considered only when all of the following hold, and every one of them is a refusal
/// otherwise:
///
/// <list type="bullet">
/// <item><description>the route, the provider profile and the model are enabled, and the account passes
/// its own domain eligibility rule for the given instant;</description></item>
/// <item><description>the route's and the model's health is one this build will route through - Healthy,
/// Degraded or ForcedEnabled, and nothing else, including a value this build does not recognise;</description></item>
/// <item><description>the route is internally coherent, so the account and the model belong to the very
/// provider profile the route names;</description></item>
/// <item><description>it carries a complete gateway identity in the same form the observation uses - the
/// three native names, or a gateway route key. The two forms are matched separately and never mixed.</description></item>
/// </list>
/// <para><b>Why a missing mode dimension is a refusal and not a wildcard.</b>
///
/// A route's identity is its provider, account, model <em>and</em> the three mode dimensions together, and
/// an observation has to carry all of them to be matched against that. If the gateway reported a native
/// provider, account and model but stayed silent about, say, the speed mode, then the routes sharing that
/// triple may differ only in the dimension nobody reported - and this build cannot tell, because it has no
/// way to know that the library happens to hold one such route today. Resolving on that basis would make
/// the answer depend on a coincidence of the operator's current library rather than on anything the
/// response said, and the same observation against a library with one more row would flip the answer. So the
/// resolver refuses before it reads a row, names the dimensions the response omitted, and lets the operator
/// decide whether the gateway contract is sufficient.
/// </para>
/// <para>
/// The one form that does not need the three dimensions is the gateway route key. A key is an observed
/// identity in its own right rather than a decomposition of the route into parts, so a response carrying
/// one can be matched on it alone. That is the whole difference between the two forms, and the two are never
/// mixed: an observation carrying both a key and a triple is matched on its key.
/// </para>
/// <para><b>What is never used to fill a gap.</b>
///
/// Not <c>Accounts.ProviderNativeId</c>, which is a Codex path or an AGY profile label. Not
/// <c>Models.ProviderModelId</c>, which may be a requested alias. Not <c>Routes.Id</c>, which is this
/// application's own key and is never compared to a gateway route key. And nothing that was requested:
/// the observation type has no member a requested value could arrive in, so there is no path by which a
/// request, a pinned role binding or a client-supplied session id can stand in for what the response
/// reported.
/// </para>
/// </summary>
public static class GatewayRouteIdentityResolver
{
    /// <summary>
    /// The health states this build is willing to route through. It is the same set
    /// <see cref="Domain.Entities.Account.IsEligibleForRouting"/> accepts, restated here because the route
    /// and the model are judged by the same standard and their own rules do not cover each other.
    /// <para>
    /// Everything else is excluded, including <see cref="HealthState.ProbeRequired"/> and
    /// <see cref="HealthState.Recovering"/>. A scope whose health is not yet established has not been
    /// shown to work, and a diagnostic that confirmed an identity by way of a scope nobody has verified
    /// would be confirming it against a guess.
    /// </para>
    /// </summary>
    private static bool IsRoutable(HealthState health) =>
        health is HealthState.Healthy or HealthState.Degraded or HealthState.ForcedEnabled;

    /// <summary>
    /// Resolves an observation that has already been folded out of its chunks.
    /// </summary>
    public static GatewayRouteResolution Resolve(
        GatewayRouteObservation observation,
        IReadOnlyList<GatewayRouteCandidate> candidates,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!observation.NamesAnIdentity)
        {
            return GatewayRouteResolution.Refused(
                GatewayRouteRefusalKind.NoNativeIdentityObserved,
                "The response carried neither all three gateway-native names nor a gateway route key, so it "
                    + "does not identify a route; a partial set of names is a name that matches more routes, "
                    + "not a shorter identity.");
        }

        // Checked before a single row is read, and only for the native-name form. The route-key form carries
        // an observed identity of its own, so it does not need the dimensions decomposed out of it.
        var unobserved = observation.UnobservedDimensions;

        if (unobserved.Count > 0 && !observation.IsRouteKeyForm)
        {
            return GatewayRouteResolution.Refused(
                GatewayRouteRefusalKind.UnobservedModeDimension,
                "The response identified a provider, an account and a model but did not report "
                    + $"{string.Join(", ", unobserved)}, and a route's identity is its three native names together "
                    + "with all three of those mode dimensions. The rows sharing this triple may differ only in a "
                    + "dimension the response was silent about, so no route is named rather than one inferred "
                    + "from a single surviving row.");
        }

        var eligible = candidates
            .Where(candidate => IsEligible(candidate, now))
            .ToArray();

        // The unbound report is form-specific, because the two forms consult different storage. A row with
        // three native names and no gateway route key is a complete identity in one form and an absent one
        // in the other, and reporting it as bound when the observation used a key - or as unbound when the
        // observation used names - would name the wrong thing for the operator to go and fix.
        var unbound = eligible
            .Where(candidate => observation.IsRouteKeyForm
                ? candidate.Route.GatewayRouteKey is null
                : !candidate.HasEveryNativeName && AgreesOnDeclaredNames(observation, candidate))
            .Select(candidate => candidate.Route.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var matches = observation.IsRouteKeyForm
            ? eligible
                .Where(candidate => MatchesRouteKey(observation, candidate))
                .ToArray()
            : eligible
                .Where(candidate => MatchesNativeNames(observation, candidate))
                .ToArray();

        if (matches.Length == 0)
        {
            return unbound.Length > 0
                ? GatewayRouteResolution.Refused(
                    GatewayRouteRefusalKind.IncompletePersistedIdentity,
                    observation.IsRouteKeyForm
                        ? "No eligible persisted route carries the observed gateway route key, and "
                            + $"{unbound.Length} eligible route(s) record no gateway route key at all, so the "
                            + "operator's library holds no row this observation could be matched against."
                        : "No eligible persisted route matched the observed gateway identity, but "
                            + $"{unbound.Length} eligible row(s) agree with everything they do declare and record no "
                            + "gateway-native name, so the operator's library holds the right rows with their "
                            + "gateway identity still unbound.",
                    unboundRouteIds: unbound)
                : GatewayRouteResolution.Refused(
                    GatewayRouteRefusalKind.NoEligibleRoute,
                    "No enabled, healthy, internally coherent persisted route carries the observed gateway "
                        + "identity, so the response is not attributable to a route.");
        }

        if (matches.Length > 1)
        {
            return Ambiguous(observation, matches);
        }

        var identified = matches[0].Route.Id;

        return GatewayRouteResolution.Resolved(
            identified,
            "The observed gateway identity matches exactly one eligible persisted route. This is a "
                + "diagnostic answer only: it is not provenance, nothing was dispatched, and any use of it "
                + "has to re-resolve against the assignment the turn actually holds.",
            new[] { identified });
    }

    /// <summary>
    /// Resolves an observation straight from the synthetic chunks that produced it, refusing a self
    /// contradictory response before a single row is read.
    /// </summary>
    public static GatewayRouteResolution Resolve(
        IReadOnlyList<GatewayRouteObservationChunk> chunks,
        IReadOnlyList<GatewayRouteCandidate> candidates,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var merged = GatewayRouteObservation.Merge(chunks);

        return merged.Agrees
            ? Resolve(merged.Observation!, candidates, now)
            : GatewayRouteResolution.Refused(
                GatewayRouteRefusalKind.ConflictingObservationChunks,
                merged.Conflict!);
    }

    /// <summary>
    /// The two refusals that are the same fact - more than one eligible row survived - separated so a
    /// caller can tell a genuine namespace collision from a set of rows no observed value distinguishes.
    /// <para>
    /// There is no mode-variant case here any more, and there is deliberately no way to reach one: in the
    /// native-name form all three dimensions are observed before a row is matched, so two survivors agree on
    /// every dimension this build can see. In the route-key form the dimensions are not consulted at all, so
    /// a key collision is not a mode question either.
    /// </para>
    /// </summary>
    private static GatewayRouteResolution Ambiguous(
        GatewayRouteObservation observation,
        IReadOnlyList<GatewayRouteCandidate> matches)
    {
        var routeIds = matches.Select(candidate => candidate.Route.Id).ToArray();
        var tuples = matches.Select(candidate => candidate.NamespaceTuple).Distinct().ToArray();

        if (tuples.Length > 1)
        {
            return GatewayRouteResolution.Refused(
                GatewayRouteRefusalKind.AmbiguousAcrossNamespaces,
                "The observed gateway-native names match "
                    + $"{tuples.Length} different provider/account/model triples across {routeIds.Length} eligible "
                    + "routes, so one observed name would describe more than one stored thing and the response "
                    + "cannot be attributed to either.",
                candidateRouteIds: routeIds);
        }

        return GatewayRouteResolution.Refused(
            GatewayRouteRefusalKind.AmbiguousAcrossRoutes,
            "The observed gateway identity matches one provider/account/model triple and every dimension this "
                + $"build can observe, yet {routeIds.Length} eligible routes occupy it, so they cannot be told "
                + "apart by any value that was observed.",
            candidateRouteIds: routeIds);
    }

    /// <summary>
    /// Whether the row could be the route a turn ran, judged on everything this build is able to observe.
    /// <para>
    /// Public because the completeness report has to agree with the resolver, and a report whose notion of
    /// eligibility differed from the answer it is a precondition for would be worse than no report. It says
    /// nothing about whether the row's gateway identity is recorded, which is a separate question.
    /// </para>
    /// </summary>
    public static bool IsEligible(GatewayRouteCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.Route.IsEnabled || !candidate.Profile.IsEnabled || !candidate.Model.IsEnabled)
        {
            return false;
        }

        if (!IsRoutable(candidate.Route.Health) || !IsRoutable(candidate.Model.Health))
        {
            return false;
        }

        if (!candidate.IsInternallyCoherent)
        {
            return false;
        }

        // The account's own rule already covers enabled, health, auth state, cooldown and manual disable, and
        // re-deriving any part of it here would be a second answer to a question the domain has.
        return candidate.Account.IsEligibleForRouting(now);
    }

    /// <summary>
    /// The three native names, each of which the row must actually declare. A row that declares none of the
    /// three is not a near miss; it is unbound, and it is handled as such.
    /// </summary>
    private static bool MatchesNativeNames(
        GatewayRouteObservation observation,
        GatewayRouteCandidate candidate)
    {
        if (!candidate.HasEveryNativeName)
        {
            return false;
        }

        return string.Equals(candidate.Profile.GatewayNativeId, observation.NativeProviderId, StringComparison.Ordinal)
            && string.Equals(candidate.Account.GatewayNativeId, observation.NativeAccountId, StringComparison.Ordinal)
            && string.Equals(candidate.Model.GatewayNativeId, observation.NativeModelId, StringComparison.Ordinal)
            && MatchesModes(observation, candidate);
    }

    /// <summary>
    /// The gateway route key, matched against the row's own key and against nothing else. In particular the
    /// row's <c>Routes.Id</c> is never compared: the two live in different namespaces, so a coincidence
    /// between them would otherwise be indistinguishable from an identity match.
    /// </summary>
    private static bool MatchesRouteKey(
        GatewayRouteObservation observation,
        GatewayRouteCandidate candidate) =>
        candidate.Route.GatewayRouteKey is { } routeKey
        && string.Equals(routeKey, observation.GatewayRouteKey, StringComparison.Ordinal);

    /// <summary>
    /// All three observed dimensions must equal the row's. There is no wildcard here and no way to
    /// substitute an unobserved dimension: the caller has already refused an observation that left one out,
    /// so a null reaching this point would mean the check above had been bypassed rather than that the
    /// dimension was optional. A row whose own column is null therefore matches no observation at all, which
    /// is the intended reading: the row does not declare the mode, so no complete observation is about it.
    /// </summary>
    private static bool MatchesModes(GatewayRouteObservation observation, GatewayRouteCandidate candidate)
    {
        var binding = candidate.Route.Binding;

        return string.Equals(observation.ReasoningEffort, binding.ReasoningEffort, StringComparison.Ordinal)
            && string.Equals(observation.SpeedMode, binding.SpeedMode, StringComparison.Ordinal)
            && string.Equals(observation.ExecutionMode, binding.ExecutionMode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether every native name the row does declare equals the observed one. Used only to recognise a
    /// row that is the right row with its identity not yet recorded, so a null on either side is agreement
    /// about that dimension and a disagreement on any declared one is not.
    /// </summary>
    private static bool AgreesOnDeclaredNames(
        GatewayRouteObservation observation,
        GatewayRouteCandidate candidate) =>
        Agreed(candidate.Profile.GatewayNativeId, observation.NativeProviderId)
        && Agreed(candidate.Account.GatewayNativeId, observation.NativeAccountId)
        && Agreed(candidate.Model.GatewayNativeId, observation.NativeModelId);

    private static bool Agreed(string? stored, string? observed) =>
        stored is null || string.Equals(stored, observed, StringComparison.Ordinal);
}
