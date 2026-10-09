using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Application.Workflows.Legacy;

/// <summary>
/// Runs one user-declared legacy entrypoint as a single supervised opaque process tree inside a
/// scratch copy, under the checkout writer lock. The runner never calls Codex or AGY directly and
/// never reports an unproven native session, route, role, or stage (ТЗ §6.15, ROADMAP Phase 10B).
/// </summary>
public interface ISupervisedLegacyWorkflowRunner
{
    /// <summary>Retries retained resource cleanup only after native cleanup has been independently confirmed.</summary>
    Task<int> RetryPendingCleanupsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    Task<LegacyWorkflowExecutionResult> ExecuteAsync(
        LegacyWorkflowExecutionRequest request,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default);
}
