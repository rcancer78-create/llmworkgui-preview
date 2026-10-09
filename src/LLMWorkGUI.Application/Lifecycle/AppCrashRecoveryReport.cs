namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>
/// Evidence of one crash/reboot recovery pass. Every list is ordered and contains only identifiers;
/// no prompts, file contents or secrets are part of the report (ТЗ §9.3).
/// </summary>
public sealed record AppCrashRecoveryReport
{
    public required DateTimeOffset RunAtUtc { get; init; }

    public required string ApplicationInstanceId { get; init; }

    public required bool SkippedAsViewOnly { get; init; }

    public required IReadOnlyList<string> InterruptedExecutionIds { get; init; }

    public required IReadOnlyList<string> InterruptedSessionIds { get; init; }

    public required IReadOnlyList<string> InterruptedWorkflowRunIds { get; init; }

    public required IReadOnlyList<string> ReleasedLockIds { get; init; }

    public required IReadOnlyList<string> RetainedLockIds { get; init; }

    public required int RehydratedHealthScopeCount { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public bool RecoveredAnything =>
        InterruptedExecutionIds.Count > 0
        || InterruptedSessionIds.Count > 0
        || InterruptedWorkflowRunIds.Count > 0
        || ReleasedLockIds.Count > 0;
}
