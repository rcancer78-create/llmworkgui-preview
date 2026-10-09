using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class Project
{
    public Project(
        string id,
        string displayName,
        string rootPath,
        string? gitBranch,
        bool isDirty,
        bool hasRequiredInstructions,
        string? defaultWorkflowId,
        string? defaultRoutePolicyId,
        DataClassification dataClassification)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        RootPath = DomainGuard.NotBlank(rootPath, nameof(rootPath));
        GitBranch = DomainGuard.OptionalNotBlank(gitBranch, nameof(gitBranch));
        IsDirty = isDirty;
        HasRequiredInstructions = hasRequiredInstructions;
        DefaultWorkflowId = DomainGuard.OptionalNotBlank(defaultWorkflowId, nameof(defaultWorkflowId));
        DefaultRoutePolicyId = DomainGuard.OptionalNotBlank(defaultRoutePolicyId, nameof(defaultRoutePolicyId));
        DataClassification = dataClassification;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string RootPath { get; }

    public string? GitBranch { get; }

    public bool IsDirty { get; }

    public bool HasRequiredInstructions { get; }

    public string? DefaultWorkflowId { get; }

    public string? DefaultRoutePolicyId { get; }

    public DataClassification DataClassification { get; }
}
