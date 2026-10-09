namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowDiffService
{
    /// <summary>
    /// Compares the pristine source directory against the candidate scratch directory and produces a
    /// file-level unified diff with added/modified/deleted statistics.
    /// </summary>
    Task<WorkflowPackageDiff> CompareAsync(
        string sourceDirectory,
        string candidateDirectory,
        CancellationToken cancellationToken = default);
}
