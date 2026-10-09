namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowFilePreview(
    string BlobId,
    string Path,
    long SizeBytes,
    bool IsBinary,
    bool IsTruncated,
    string? Content);
