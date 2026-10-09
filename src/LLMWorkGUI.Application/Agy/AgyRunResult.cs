using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Result of one native AGY CLI execution (ТЗ §6.11a). Success requires a confirmed
/// init, observed model and a non-error terminal result event in addition to a clean
/// exit; exit code 0 or a partial stream alone is never success.
/// </summary>
public sealed record AgyRunResult
{
    public required bool IsSuccess { get; init; }

    public string? FailureReason { get; init; }

    public bool InitConfirmed { get; init; }

    public string? ConversationId { get; init; }

    public string? ObservedModel { get; init; }

    public IReadOnlyList<string> GrantedPermissions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<AgyToolEvent> ToolEvents { get; init; } = Array.Empty<AgyToolEvent>();

    public AgyTerminalEvent? TerminalResult { get; init; }

    public ProcessExecutionResult? ProcessResult { get; init; }
}
