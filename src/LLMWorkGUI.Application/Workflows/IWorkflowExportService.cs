namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowExportService
{
    Task<Stream> OpenVersionExportStreamAsync(
        string versionId,
        CancellationToken cancellationToken = default);

    Task ExportToFileAsync(
        string versionId,
        string destinationFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes only to an absent destination; the final move must refuse a competing file.</summary>
    Task ExportToNewFileAsync(string versionId, string destinationFilePath, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Create-only workflow export is unavailable."));
}
