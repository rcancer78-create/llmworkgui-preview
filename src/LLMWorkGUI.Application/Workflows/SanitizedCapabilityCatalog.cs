using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record SanitizedProviderInfo(
    string ProviderId,
    string DisplayName,
    BackendType Backend,
    bool IsEnabled);

public sealed record SanitizedModelCapabilities(ModelCapabilityFlags Flags,
    IReadOnlyList<string> ReasoningEfforts, IReadOnlyList<string> SpeedModes, int? ContextLimit)
{
    public static SanitizedModelCapabilities Unknown { get; } = new(ModelCapabilityFlags.None, [], [], null);
}

public sealed record SanitizedModelInfo(
    string ModelId,
    string DisplayName,
    ModelCapabilityFlags Capabilities,
    IReadOnlyList<string> SupportedReasoningEfforts,
    IReadOnlyList<string> SupportedSpeedModes,
    int? ContextWindow,
    HealthState Health,
    bool IsRoutable)
{
    /// <summary>
    /// Local account record id behind this row. This is the catalog key, equal to
    /// <see cref="ModelId"/> for rows produced by <c>SanitizedCatalogProvider</c>. It is an
    /// identifier of our own record and must never be sent to a backend as a model id.
    /// </summary>
    public string? AccountId { get; init; }

    /// <summary>Provider profile that owns the account; the backend is resolved from this profile.</summary>
    public string? ProviderProfileId { get; init; }

    /// <summary>Backend the owning provider profile resolves to. Null when unknown.</summary>
    public BackendType? Backend { get; init; }

    /// <summary>
    /// Backend-native model id for the account. Null when neither the local account record
    /// (<c>Account.ProviderNativeId</c>) nor the local provider configuration supplies one, in which
    /// case the row is not a usable adaptation target: the account id is never substituted for it.
    /// </summary>
    public string? BackendModelId { get; init; }

    /// <summary>
    /// Every backend-native model id that is really selectable for this account, in configuration
    /// order. A normal OpenCode account carries no <c>ProviderNativeId</c>, so the ids come from the
    /// local provider configuration; the account record is used only when the configuration has none.
    /// One account with several configured models therefore contributes one real account+model route
    /// per entry, and the selected route's model is exactly the id in this list.
    /// </summary>
    public IReadOnlyList<string> SelectableBackendModelIds
    {
        get
        {
            if (BackendModelIds is { Count: > 0 })
            {
                return BackendModelIds;
            }

            return string.IsNullOrWhiteSpace(BackendModelId)
                ? Array.Empty<string>()
                : new[] { BackendModelId };
        }
    }

    /// <summary>Configured backend model ids, empty when the row has none.</summary>
    public IReadOnlyList<string> BackendModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Exact native-model capabilities. Null is reserved for explicitly constructed legacy
    /// catalogs; production projections always populate this map, including unknown entries.</summary>
    public IReadOnlyDictionary<string, SanitizedModelCapabilities>? BackendCapabilities { get; init; }

    public SanitizedModelCapabilities CapabilitiesFor(string nativeModelId) => BackendCapabilities is null
        ? new(Capabilities, SupportedReasoningEfforts, SupportedSpeedModes, ContextWindow)
        : BackendCapabilities.TryGetValue(nativeModelId, out var value) ? value : SanitizedModelCapabilities.Unknown;
}

public sealed record SanitizedCapabilityCatalog
{
    public SanitizedCapabilityCatalog(
        IReadOnlyList<SanitizedProviderInfo> providers,
        IReadOnlyList<SanitizedModelInfo> models,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(models);

        Providers = providers.ToArray();
        Models = models.ToArray();
        GeneratedAtUtc = generatedAtUtc;
    }

    public IReadOnlyList<SanitizedProviderInfo> Providers { get; }

    public IReadOnlyList<SanitizedModelInfo> Models { get; }

    public DateTimeOffset GeneratedAtUtc { get; }
}
