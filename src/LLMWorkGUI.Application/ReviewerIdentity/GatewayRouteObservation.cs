namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// The single, non-contradictory response-origin identity a set of synthetic chunks agrees on.
/// <para>
/// Two chunks that report different values for the same field is a contradiction, not a later revision, and
/// the merge is what says so. Nothing here decides which chunk wins: a stream that contradicts itself about
/// which provider or which model answered has not identified anything, and picking the last one would be
/// exactly the substitution the reviewer channel refuses.
/// </para>
/// </summary>
public sealed record GatewayRouteObservation
{
    private GatewayRouteObservation(
        string? nativeProviderId,
        string? nativeAccountId,
        string? nativeModelId,
        string? reasoningEffort,
        string? speedMode,
        string? executionMode,
        string? gatewayRouteKey)
    {
        NativeProviderId = nativeProviderId;
        NativeAccountId = nativeAccountId;
        NativeModelId = nativeModelId;
        ReasoningEffort = reasoningEffort;
        SpeedMode = speedMode;
        ExecutionMode = executionMode;
        GatewayRouteKey = gatewayRouteKey;
    }

    public string? NativeProviderId { get; }

    public string? NativeAccountId { get; }

    public string? NativeModelId { get; }

    public string? ReasoningEffort { get; }

    public string? SpeedMode { get; }

    public string? ExecutionMode { get; }

    public string? GatewayRouteKey { get; }

    /// <summary>
    /// True when the observation carries a gateway route key, which is the form the resolver matches. The
    /// three native names are then not used for matching at all, even when the gateway reported them too: a
    /// response that names both a key and a triple is matched on its key, and mixing the two would let a
    /// route be identified by a key its row does not carry while its names happen to agree.
    /// </summary>
    public bool IsRouteKeyForm => GatewayRouteKey is not null;

    /// <summary>
    /// True when the three native names are all present. A partial set is not an identity: two of three is
    /// a shorter name, and a shorter name matches more rows than a complete one.
    /// </summary>
    public bool HasEveryNativeName =>
        NativeProviderId is not null && NativeAccountId is not null && NativeModelId is not null;

    /// <summary>
    /// True when the observation names a complete identity in one of the two accepted forms. Blank values
    /// never count, so a whitespace-only chunk is an absence and not a match against a whitespace column.
    /// </summary>
    public bool NamesAnIdentity => IsRouteKeyForm || HasEveryNativeName;

    /// <summary>
    /// The mode dimensions the observation left unobserved, in a fixed order. The native-name form refuses
    /// whenever this is not empty, so it exists to name what the response omitted; the route-key form ignores
    /// it, because a gateway route key is an observed identity rather than a decomposition into parts.
    /// </summary>
    public IReadOnlyList<string> UnobservedDimensions
    {
        get
        {
            var missing = new List<string>(3);

            if (ReasoningEffort is null)
            {
                missing.Add("reasoningEffort");
            }

            if (SpeedMode is null)
            {
                missing.Add("speedMode");
            }

            if (ExecutionMode is null)
            {
                missing.Add("executionMode");
            }

            return missing;
        }
    }

    /// <summary>
    /// Folds a set of synthetic chunks into one observation, or reports the first contradiction it finds.
    /// <para>
    /// Merging is over the whole sequence, not pairwise, so a contradiction between the first and third
    /// chunk is caught even when the second carried nothing. An empty sequence is an observation with
    /// nothing in it, which resolves to a refusal rather than to a wildcard match against every row.
    /// </para>
    /// </summary>
    public static GatewayRouteObservationMerge Merge(IReadOnlyList<GatewayRouteObservationChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var observedChunks = chunks.ToArray();

        string? provider = null;
        string? account = null;
        string? model = null;
        string? reasoning = null;
        string? speed = null;
        string? execution = null;
        string? routeKey = null;

        for (var index = 0; index < observedChunks.Length; index++)
        {
            var chunk = observedChunks[index];

            if (chunk is null)
            {
                return GatewayRouteObservationMerge.Conflicting(
                    $"chunk {index} is absent, so the sequence cannot be read as one observation");
            }

            if (!TryMerge("native provider", chunk.NativeProviderId, ref provider, index, out var conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("native account", chunk.NativeAccountId, ref account, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("native model", chunk.NativeModelId, ref model, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("reasoning effort", chunk.ReasoningEffort, ref reasoning, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("speed mode", chunk.SpeedMode, ref speed, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("execution mode", chunk.ExecutionMode, ref execution, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }

            if (!TryMerge("gateway route key", chunk.GatewayRouteKey, ref routeKey, index, out conflict))
            {
                return GatewayRouteObservationMerge.Conflicting(conflict!);
            }
        }

        return GatewayRouteObservationMerge.Agreeing(
            new GatewayRouteObservation(provider, account, model, reasoning, speed, execution, routeKey), observedChunks);
    }

    /// <summary>
    /// A later value that differs from an established one is a conflict; a null adds nothing, and an equal
    /// value is simply the same answer arriving twice.
    /// </summary>
    private static bool TryMerge(
        string field,
        string? incoming,
        ref string? established,
        int index,
        out string? conflict)
    {
        conflict = null;

        if (incoming is null)
        {
            return true;
        }

        if (established is null)
        {
            established = incoming;
            return true;
        }

        if (string.Equals(established, incoming, StringComparison.Ordinal))
        {
            return true;
        }

        conflict = $"chunk {index} reports a conflicting {field}, so the response contradicts itself "
            + "and identifies nothing";

        return false;
    }
}

/// <summary>
/// The outcome of folding synthetic chunks: either the one identity they agree on, or the contradiction
/// that stopped the fold. There is no third answer, and in particular no "closest" one.
/// </summary>
public sealed record GatewayRouteObservationMerge
{
    private static readonly IReadOnlyList<GatewayRouteObservationChunk> None =
        Array.Empty<GatewayRouteObservationChunk>();

    private GatewayRouteObservationMerge(
        GatewayRouteObservation? observation,
        IReadOnlyList<GatewayRouteObservationChunk> chunks,
        string? conflict)
    {
        Observation = observation;
        Chunks = chunks;
        Conflict = conflict;
    }

    public GatewayRouteObservation? Observation { get; }

    /// <summary>
    /// The chunks that were read. Empty on a conflict, because a contradicting sequence is not partially
    /// usable and handing back the prefix that happened to agree would invite exactly the reading the
    /// resolver refuses.
    /// </summary>
    public IReadOnlyList<GatewayRouteObservationChunk> Chunks { get; }

    public string? Conflict { get; }

    public bool Agrees => Observation is not null;

    public static GatewayRouteObservationMerge Agreeing(GatewayRouteObservation observation,
        IReadOnlyList<GatewayRouteObservationChunk>? chunks = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new GatewayRouteObservationMerge(observation,
            chunks is null ? None : Array.AsReadOnly(chunks.ToArray()), null);
    }

    public static GatewayRouteObservationMerge Conflicting(string conflict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conflict);

        return new GatewayRouteObservationMerge(null, None, conflict);
    }
}
