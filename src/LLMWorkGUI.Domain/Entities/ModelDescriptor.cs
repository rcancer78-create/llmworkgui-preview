using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class ModelDescriptor
{
    public ModelDescriptor(
        string id,
        BackendType backend,
        string providerProfileId,
        string providerModelId,
        string displayName,
        IReadOnlyList<string> availableAccountIds,
        IReadOnlyList<string> supportedReasoningEfforts,
        IReadOnlyList<string> supportedSpeedModes,
        IReadOnlyList<string> supportedModes,
        CapabilityState capabilityState,
        ModelProvenance provenance,
        bool isEnabled,
        HealthState health,
        int? contextLimit,
        bool supportsTools,
        bool supportsAttachments,
        DateTimeOffset discoveredAt,
        string? gatewayNativeId = null)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        Backend = backend;
        ProviderProfileId = DomainGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        ProviderModelId = DomainGuard.NotBlank(providerModelId, nameof(providerModelId));
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        AvailableAccountIds = DomainGuard.NotNullList(availableAccountIds, nameof(availableAccountIds));
        SupportedReasoningEfforts = DomainGuard.NotNullList(supportedReasoningEfforts, nameof(supportedReasoningEfforts));
        SupportedSpeedModes = DomainGuard.NotNullList(supportedSpeedModes, nameof(supportedSpeedModes));
        SupportedModes = DomainGuard.NotNullList(supportedModes, nameof(supportedModes));
        CapabilityState = capabilityState;
        Provenance = provenance;
        IsEnabled = isEnabled;
        Health = health;
        ContextLimit = contextLimit;
        SupportsTools = supportsTools;
        SupportsAttachments = supportsAttachments;
        DiscoveredAt = discoveredAt;
        GatewayNativeId = DomainGuard.OptionalNotBlank(gatewayNativeId, nameof(gatewayNativeId));
    }

    public string Id { get; }

    public BackendType Backend { get; }

    public string ProviderProfileId { get; }

    public string ProviderModelId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<string> AvailableAccountIds { get; }

    public IReadOnlyList<string> SupportedReasoningEfforts { get; }

    public IReadOnlyList<string> SupportedSpeedModes { get; }

    public IReadOnlyList<string> SupportedModes { get; }

    public CapabilityState CapabilityState { get; }

    public ModelProvenance Provenance { get; }

    public bool IsEnabled { get; }

    public HealthState Health { get; }

    public int? ContextLimit { get; }

    public bool SupportsTools { get; }

    public bool SupportsAttachments { get; }

    public DateTimeOffset DiscoveredAt { get; }

    /// <summary>
    /// The model id a gateway reports for the model that actually ran, when one has been observed and
    /// stored.
    /// <para>
    /// This is NOT <see cref="ProviderModelId"/> and must never be read as it. <see cref="ProviderModelId"/>
    /// is what a request names, and a gateway maps a requested alias onto a real model, with fallbacks: the
    /// alias is the question, and a response-origin id is the answer. Matching the two would let a request
    /// decide which model is treated as having run. Null means no native model id is recorded.
    /// </para>
    /// </summary>
    public string? GatewayNativeId { get; }
}
