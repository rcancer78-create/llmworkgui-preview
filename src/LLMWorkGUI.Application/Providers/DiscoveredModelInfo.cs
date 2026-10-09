namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Model discovered dynamically from a provider endpoint (e.g. /v1/models).
/// </summary>
public sealed record DiscoveredModelInfo(
    string Id,
    string? Name = null,
    string? OwnedBy = null);
