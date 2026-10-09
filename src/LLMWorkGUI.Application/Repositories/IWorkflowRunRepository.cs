using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Application.Repositories;

public interface IWorkflowRunRepository
{
    /// <summary>Refuses unless the stored successful execution belongs to this run and project.</summary>
    Task ValidateArtifactExecutionAsync(WorkflowRun run, string executionId,
        CancellationToken cancellationToken = default) =>
        throw new WorkflowValidationException("This repository cannot verify artifact execution ownership.");

    Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a transition only while the expected aggregate and artifact set are current, rechecking
    /// execution associations in the same transaction. Lightweight stores cannot authorize linked artifacts.
    /// </summary>
    Task SaveTransitionAsync(WorkflowRun expected, WorkflowRun next, CancellationToken cancellationToken = default)
    {
        if (expected.Artifacts.Any(a => a.ExecutionId is not null))
            throw new WorkflowValidationException("This repository cannot atomically authorize execution-associated artifacts.");
        return SaveAsync(next, cancellationToken);
    }

    /// <summary>
    /// Commits the artifact row and the run's evidence state as one unit. The run passed in is the copy that
    /// carries the evidence, so an implementation that rolls the transaction back leaves the caller holding a
    /// run that cannot authorize anything on the evidence it failed to persist.
    /// </summary>
    Task SaveArtifactAsync(
        WorkflowRun run,
        WorkflowArtifactEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetActiveByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default);
}
