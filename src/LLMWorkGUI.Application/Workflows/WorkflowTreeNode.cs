namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowTreeNode(
    string Path,
    string Name,
    bool IsDirectory,
    long SizeBytes,
    long CompressedSizeBytes,
    DateTimeOffset LastModifiedUtc);
