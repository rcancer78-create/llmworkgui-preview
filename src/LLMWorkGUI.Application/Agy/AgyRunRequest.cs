namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Specification of one native AGY CLI execution (ТЗ §6.11a).
/// The prompt is delivered in print mode with a machine-readable stream; no hidden
/// OpenCode plugin fallback is permitted for this route.
/// </summary>
public sealed class AgyRunRequest
{
    public const string ModeAcceptEdits = "accept-edits";
    public const string ModePlan = "plan";

    public required string ExecutionId { get; init; }

    /// <summary>Project checkout used as the agy working directory.</summary>
    public required string ProjectDirectory { get; init; }

    public required string ModelId { get; init; }

    /// <summary>One of <see cref="ModeAcceptEdits"/> or <see cref="ModePlan"/>.</summary>
    public required string Mode { get; init; }

    public string Prompt { get; init; } = string.Empty;

    /// <summary>Existing native conversation to continue, or null for a new conversation.</summary>
    public string? ConversationId { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>Bounded wall-clock timeout for the execution.</summary>
    public TimeSpan? Timeout { get; init; }
}
