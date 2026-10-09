namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Provider/account/model/session values the gateway reported in a response, plus whether each one
/// was independently verified. Missing values stay null: an unproven account context remains
/// opaque and must never be synthesized (ТЗ §6.4, ADR-0007 §8).
/// <para>
/// The four strings are <b>response claims</b>, not observed native identity. Installed
/// star-cliproxy v1.3.0 answers an OpenAI-compatible <c>/v1/chat/completions</c> request by echoing
/// the <c>model</c> the caller sent, and its provider/account/session response headers have no
/// proven contract behind them either. A claim that matches the request therefore proves nothing:
/// the same bytes would be produced by a gateway that substituted the route.
/// </para>
/// <para>
/// Each trailing boolean says only that the claim beside it was verified from a source other than
/// the response body. They default to <c>false</c>, so the legacy four-argument form stays
/// unverified and a field's own trust never confers trust on another. No source in this build
/// establishes a verified value, so in production every flag is <c>false</c>; the flags exist to
/// state that fact per field rather than to imply it. They are descriptive data and are never an
/// authorization path.
/// </para>
/// </summary>
public sealed record StarCliProxyObservedEvidence(
    string? ProviderId = null,
    string? AccountId = null,
    string? ModelId = null,
    string? SessionId = null,
    bool IsProviderVerified = false,
    bool IsAccountVerified = false,
    bool IsModelVerified = false,
    bool IsSessionVerified = false)
{
    public static StarCliProxyObservedEvidence Empty { get; } = new();

    public bool IsEmpty =>
        ProviderId is null && AccountId is null && ModelId is null && SessionId is null;

    /// <summary>
    /// Merges newer non-null evidence over this instance without erasing known values. Each
    /// verification flag travels only with its own surviving value: a newer non-null value takes
    /// the newer flag, an absent newer value retains the older value together with the older flag,
    /// and a null value is never verified.
    /// </summary>
    public StarCliProxyObservedEvidence Merge(StarCliProxyObservedEvidence? newer)
    {
        if (newer is null)
        {
            return this;
        }

        var providerId = newer.ProviderId ?? ProviderId;
        var accountId = newer.AccountId ?? AccountId;
        var modelId = newer.ModelId ?? ModelId;
        var sessionId = newer.SessionId ?? SessionId;

        return new StarCliProxyObservedEvidence(
            providerId,
            accountId,
            modelId,
            sessionId,
            SurvivingVerification(providerId, newer.ProviderId, newer.IsProviderVerified, IsProviderVerified),
            SurvivingVerification(accountId, newer.AccountId, newer.IsAccountVerified, IsAccountVerified),
            SurvivingVerification(modelId, newer.ModelId, newer.IsModelVerified, IsModelVerified),
            SurvivingVerification(sessionId, newer.SessionId, newer.IsSessionVerified, IsSessionVerified));
    }

    /// <summary>
    /// The flag that belongs to the value which actually survived the merge. A value that is absent
    /// keeps the flag it already had; a value replaced by the newer one takes the newer flag.
    /// </summary>
    private static bool SurvivingVerification(
        string? survivingValue,
        string? newerValue,
        bool newerVerified,
        bool existingVerified) =>
        survivingValue is not null && (newerValue is not null ? newerVerified : existingVerified);
}