namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Bitwise flags representing capability features supported by a specific LLM model.
/// </summary>
[Flags]
public enum ModelCapabilityFlags
{
    None = 0,
    Chat = 1 << 0,
    Vision = 1 << 1,
    ToolCalling = 1 << 2,
    ReasoningVariants = 1 << 3
}
