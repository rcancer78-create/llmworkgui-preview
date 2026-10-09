namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowSecretScanner
{
    Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
        ScratchWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<WorkflowSecretScanReport> ScanFilesAsync(
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken = default);
}
