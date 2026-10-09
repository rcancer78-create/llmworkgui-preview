using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Infrastructure.Workflows;

// Test-only admission/copy barriers. The internal contract is inaccessible to public product callers.
internal sealed record DirectoryImportTestHooks(
    long MaxSingleFileBytes = WorkflowImportLimits.MaxSingleFileBytes,
    long MaxUncompressedTotalBytes = WorkflowImportLimits.MaxUncompressedTotalBytes,
    long MaxArchiveSizeBytes = WorkflowImportLimits.MaxArchiveSizeBytes,
    Action<string>? BeforeFileCopy = null,
    Func<string, FileStream>? OpenSource = null,
    Func<string, FileStream>? CreateSpool = null);
