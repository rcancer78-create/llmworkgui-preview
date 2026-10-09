namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Information about an installed or configured OpenCode plugin.
/// </summary>
public sealed record PluginInfo(
    string Name,
    string? Version = null,
    bool Enabled = true,
    string? Description = null);
