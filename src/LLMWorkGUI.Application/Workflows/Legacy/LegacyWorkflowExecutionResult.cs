using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Application.Workflows.Legacy;

/// <summary>
/// Process states reported for an opaque legacy execution. A legacy run never invents a native
/// session, route, role, or stage; only the supervised process lifecycle is reported.
/// </summary>
public static class LegacyProcessStates
{
    public const string Running = "Running";

    public const string Exited = "Exited";

    public const string Crashed = "Crashed";

    public const string TimedOut = "TimedOut";

    public const string Terminated = "Terminated";
    public const string StartupPending = "StartupPending";
    public const string CleanupPending = "CleanupPending";
}

/// <summary>
/// Observation of one supervised legacy entrypoint run; a pending result is not terminal evidence. The execution outcome is deliberately
/// separate from any workflow-level terminal outcome: an exit code of zero proves the process ran,
/// not that the workflow succeeded.
/// </summary>
public sealed record LegacyWorkflowExecutionResult
{
    public required string ExecutionId { get; init; }

    public string? WorkflowRunId { get; init; }

    public int? ProcessId { get; init; }

    public required string ProcessState { get; init; }

    public required ProcessTerminationReason TerminationReason { get; init; }

    public int? ExitCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Observation time for pending outcomes, not proof of physical process exit.</summary>
    public required DateTimeOffset ExitedAtUtc { get; init; }

    public required string StandardOutputLogPath { get; init; }

    public required string StandardErrorLogPath { get; init; }

    public required string StandardOutputTail { get; init; }

    public required string StandardErrorTail { get; init; }

    public required IReadOnlyList<string> DiscoveredArtifactPaths { get; init; }

    /// <summary>
    /// False when the post-operation SHA-256 check failed or was not attempted during pending cleanup. A proven source hash mismatch is
    /// a run failure, never a warning (legacy-entrypoint-boundary §3.2).
    /// </summary>
    public bool SourceBlobHashMatches { get; init; }

    public bool IsSuccess =>
        ExitCode == 0
        && TerminationReason == ProcessTerminationReason.None
        && SourceBlobHashMatches;
}
