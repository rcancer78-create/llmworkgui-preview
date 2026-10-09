using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowImportResult(
    WorkflowPackage Package,
    WorkflowVersion Version,
    string BlobId,
    bool IsDuplicate);
