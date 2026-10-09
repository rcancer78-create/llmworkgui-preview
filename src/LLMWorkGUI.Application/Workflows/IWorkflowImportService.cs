namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowImportService
{
    Task<WorkflowImportResult> ImportZipAsync(
        Stream archiveStream,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowImportResult> ImportDirectoryAsync(
        string directoryPath,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default);
}
