namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// One synthetic response-origin chunk: the gateway-native values a single terminal chunk of a reviewer
/// turn claimed, exactly as they were seen.
/// <para>
/// This type is what the gateway has to report, and nothing else. It has no member for a requested route, a
/// requested model, a requested provider, a requested account or a client-supplied session id, because each
/// of those is what the request said and the whole point of a response-origin identity is that it is not
/// the request. A blank value normalizes to null on the way in, so "the gateway reported an empty name" and
/// "the gateway reported no name" are the same absence rather than two distinguishable claims, only one of
/// which would be falsifiable.
/// </para>
/// </summary>
public sealed record GatewayRouteObservationChunk
{
    public GatewayRouteObservationChunk(
        string? nativeProviderId = null,
        string? nativeAccountId = null,
        string? nativeModelId = null,
        string? reasoningEffort = null,
        string? speedMode = null,
        string? executionMode = null,
        string? gatewayRouteKey = null)
    {
        NativeProviderId = Normalize(nativeProviderId);
        NativeAccountId = Normalize(nativeAccountId);
        NativeModelId = Normalize(nativeModelId);
        ReasoningEffort = Normalize(reasoningEffort);
        SpeedMode = Normalize(speedMode);
        ExecutionMode = Normalize(executionMode);
        GatewayRouteKey = Normalize(gatewayRouteKey);
    }

    public string? NativeProviderId { get; }

    public string? NativeAccountId { get; }

    public string? NativeModelId { get; }

    /// <summary>The observed reasoning effort, or null when the chunk did not report one.</summary>
    public string? ReasoningEffort { get; }

    /// <summary>The observed speed mode, or null when the chunk did not report one.</summary>
    public string? SpeedMode { get; }

    /// <summary>The observed execution mode, or null when the chunk did not report one.</summary>
    public string? ExecutionMode { get; }

    /// <summary>
    /// The observed gateway route key, or null. Observed separately from the three native names because a
    /// gateway that reports one unambiguous key does not have to decompose it into dimensions, and this
    /// build accepts either form - never a mixture of the two.
    /// </summary>
    public string? GatewayRouteKey { get; }

    public bool IsEmpty =>
        NativeProviderId is null
        && NativeAccountId is null
        && NativeModelId is null
        && ReasoningEffort is null
        && SpeedMode is null
        && ExecutionMode is null
        && GatewayRouteKey is null;

    /// <summary>
    /// A trimmed value, or null. Trimming is the only normalization performed: two chunks that differ only
    /// by surrounding whitespace are reporting the same name, and a name that is entirely whitespace names
    /// nothing at all.
    /// </summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
