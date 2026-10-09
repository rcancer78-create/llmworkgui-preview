namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Inventory identity with explicitly supplied capabilities. Missing flags mean
/// unconfirmed support, not a provider-reported declaration of non-support.
/// </summary>
public sealed record DiscoveredModelDetails(
    string Id,
    string Name,
    string? Description = null,
    int? ContextWindow = null,
    ModelCapabilityFlags Capabilities = ModelCapabilityFlags.None,
    IReadOnlyList<string>? SupportedReasoningEfforts = null)
{
    public IReadOnlyList<string> SupportedReasoningEfforts { get; init; } =
        SupportedReasoningEfforts ?? Array.Empty<string>();

    public bool SupportsVision => (Capabilities & ModelCapabilityFlags.Vision) != 0;
    public bool SupportsToolCalling => (Capabilities & ModelCapabilityFlags.ToolCalling) != 0;
    public bool SupportsReasoning => (Capabilities & ModelCapabilityFlags.ReasoningVariants) != 0;
}
