namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowTreePreview(
    string BlobId,
    IReadOnlyList<WorkflowTreeNode> Nodes,
    string? PrimaryDocumentationPath,
    string? PrimaryDocumentationContent,
    bool IsPrimaryDocumentationTruncated);
