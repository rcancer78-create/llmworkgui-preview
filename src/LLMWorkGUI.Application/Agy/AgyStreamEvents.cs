namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Tool/session event observed in the agy stream-json output (ТЗ §6.11a).
/// Only sanitized metadata is retained; raw tool arguments are never stored or logged.
/// </summary>
public sealed record AgyToolEvent(string Name, string? Status = null);

/// <summary>
/// Terminal result event of an agy turn (ТЗ §6.11a). A stream without this event
/// must never be treated as success, regardless of exit code.
/// </summary>
public sealed record AgyTerminalEvent(bool IsError, string? Subtype = null, string? ResultText = null);
