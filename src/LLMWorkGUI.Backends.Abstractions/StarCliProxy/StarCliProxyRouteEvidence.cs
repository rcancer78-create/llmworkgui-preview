namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Requested vs observed route evidence for one star-cliproxy turn. A mismatch on provider,
/// account or model must terminate the execution as <c>RouteMismatch</c> instead of continuing
/// the binding (ТЗ §6.4, §6.11a). The four observed values are unverified response claims: they are
/// checked here for consistency against the request, never read as proven native identity.
/// </summary>
public sealed record StarCliProxyRouteEvidence(
    string? RequestedProviderId,
    string? RequestedAccountId,
    string? RequestedModelId,
    string? RequestedSessionId,
    string? ObservedProviderId,
    string? ObservedAccountId,
    string? ObservedModelId,
    string? ObservedSessionId)
{
    /// <summary>
    /// Always <c>false</c>.
    /// <para>
    /// The observed values on this record are unverified response claims, not observed native
    /// identity. The gateway answers with generic OpenAI-compatible <c>provider</c>/<c>account</c>/
    /// <c>model</c>/<c>session_id</c> body fields and optional response headers whose provenance no
    /// proven contract establishes; the completion <c>model</c> in particular is echoed from
    /// <c>body.model</c> of the request we sent. Two non-null claims therefore cannot be read as
    /// "the gateway independently reported both provider and model": the same pair is produced by a
    /// gateway that substituted the route, and reporting completeness here would promote that echo
    /// to identity.
    /// </para>
    /// <para>
    /// Establishing completeness needs an independently observed native identity source and an
    /// authority projection over it, neither of which exists in this build; that projection is
    /// outside this slice. Partial claims keep the route opaque / ManualOnly exactly as before.
    /// </para>
    /// <para>
    /// This says nothing about <see cref="HasMismatch"/>: a claim that contradicts the requested
    /// route is still a contradiction and still terminates the turn.
    /// </para>
    /// </summary>
    public bool IsFullyObserved => false;

    /// <summary>
    /// True when any requested value is contradicted by a reported observed value. Absent
    /// observed values are never treated as a match. Claims are unverified, but a contradiction
    /// between two things the gateway itself said is still a contradiction: this stays a
    /// response-claim consistency check and keeps refusing exactly as before.
    /// </summary>
    public bool HasMismatch => MismatchReason is not null;

    public string? MismatchReason
    {
        get
        {
            if (Mismatch(RequestedProviderId, ObservedProviderId) == true)
            {
                return $"Observed provider '{ObservedProviderId}' does not match the requested provider '{RequestedProviderId}'.";
            }

            if (Mismatch(RequestedAccountId, ObservedAccountId) == true)
            {
                return $"Observed account '{ObservedAccountId}' does not match the requested account '{RequestedAccountId}'.";
            }

            if (Mismatch(RequestedModelId, ObservedModelId) == true)
            {
                return $"Observed model '{ObservedModelId}' does not match the requested model '{RequestedModelId}'.";
            }

            if (Mismatch(RequestedSessionId, ObservedSessionId) == true)
            {
                return $"Observed session '{ObservedSessionId}' does not match the requested session '{RequestedSessionId}'.";
            }

            return null;
        }
    }

    public static StarCliProxyRouteEvidence ForRequest(
        StarCliProxyChatRequest request,
        StarCliProxyObservedEvidence? observed)
    {
        ArgumentNullException.ThrowIfNull(request);

        observed ??= StarCliProxyObservedEvidence.Empty;

        return new StarCliProxyRouteEvidence(
            request.RequestedProviderId,
            request.RequestedAccountId,
            request.ModelId,
            request.RequestedSessionId,
            observed.ProviderId,
            observed.AccountId,
            observed.ModelId,
            observed.SessionId);
    }

    private static bool? Mismatch(string? requested, string? observed)
    {
        if (requested is null || observed is null)
        {
            return null;
        }

        return !string.Equals(requested, observed, StringComparison.OrdinalIgnoreCase);
    }
}
