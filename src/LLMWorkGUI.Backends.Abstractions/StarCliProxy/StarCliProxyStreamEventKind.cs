namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>Normalized kind of one streamed star-cliproxy event.</summary>
public enum StarCliProxyStreamEventKind
{
    ContentDelta,
    ReasoningDelta,
    ToolCall,
    Usage,
    Completed,
    Error,
    Malformed
}
