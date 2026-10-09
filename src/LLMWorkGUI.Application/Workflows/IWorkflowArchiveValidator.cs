using System.IO.Compression;

namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowArchiveValidator
{
    void ValidateArchive(
        ZipArchive archive,
        long archiveSizeBytes,
        CancellationToken cancellationToken = default);
}
