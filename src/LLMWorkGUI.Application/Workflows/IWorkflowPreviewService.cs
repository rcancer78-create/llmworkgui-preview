namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowPreviewService
{
    public const int MaxPreviewSizeBytes = 512 * 1024;

    Task<WorkflowTreePreview> GetTreePreviewAsync(
        string blobId,
        CancellationToken cancellationToken = default);

    Task<WorkflowFilePreview> GetFilePreviewAsync(
        string blobId,
        string relativePath,
        CancellationToken cancellationToken = default);
}
