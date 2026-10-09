using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// An immutable, versioned workflow template: the editable graph plus its role bindings and the document
/// templates the workflow requires. Saving a version never mutates another version, the active run or the
/// original imported ZIP; cloning always produces a user-owned template at version 1.
/// </summary>
public sealed class WorkflowTemplateDefinition
{
    public WorkflowTemplateDefinition(
        string templateId,
        int version,
        string displayName,
        string description,
        WorkflowGraph graph,
        IReadOnlyList<RoleBindingDefinition> roleBindings,
        IReadOnlyList<DocumentTemplateKind> requiredDocumentTemplates,
        bool isBuiltIn,
        DateTimeOffset createdAtUtc)
    {
        TemplateId = DomainGuard.NotBlank(templateId, nameof(templateId));

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A template version starts at 1.");
        }

        Version = version;
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        Description = DomainGuard.NotBlank(description, nameof(description));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        RoleBindings = CopyRoleBindings(roleBindings);
        RequiredDocumentTemplates = CopyDocumentKinds(requiredDocumentTemplates);
        IsBuiltIn = isBuiltIn;
        CreatedAtUtc = createdAtUtc;
    }

    public string TemplateId { get; }

    public int Version { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public WorkflowGraph Graph { get; }

    public IReadOnlyList<RoleBindingDefinition> RoleBindings { get; }

    public IReadOnlyList<DocumentTemplateKind> RequiredDocumentTemplates { get; }

    /// <summary>True for a shipped template; a built-in version is never overwritten by a user save.</summary>
    public bool IsBuiltIn { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string VersionDisplay => $"v{Version}";

    /// <summary>
    /// Returns a user-owned copy of this template at version 1. The source template is not mutated.
    /// </summary>
    public WorkflowTemplateDefinition Clone(string newTemplateId, string newDisplayName) =>
        new(
            newTemplateId,
            version: 1,
            newDisplayName,
            Description,
            Graph,
            RoleBindings,
            RequiredDocumentTemplates,
            isBuiltIn: false,
            CreatedAtUtc);

    /// <summary>
    /// Creates a new version of this template without mutating the source. When
    /// <paramref name="isBuiltIn"/> is omitted the built-in flag is preserved.
    /// </summary>
    public WorkflowTemplateDefinition CreateVersion(int newVersion, bool? isBuiltIn = null) =>
        new(
            TemplateId,
            newVersion,
            DisplayName,
            Description,
            Graph,
            RoleBindings,
            RequiredDocumentTemplates,
            isBuiltIn ?? IsBuiltIn,
            CreatedAtUtc);

    private static IReadOnlyList<RoleBindingDefinition> CopyRoleBindings(
        IReadOnlyList<RoleBindingDefinition> roleBindings)
    {
        ArgumentNullException.ThrowIfNull(roleBindings);

        var copy = new RoleBindingDefinition[roleBindings.Count];

        for (var index = 0; index < roleBindings.Count; index++)
        {
            copy[index] = roleBindings[index]
                ?? throw new ArgumentException(
                    "A template cannot contain null role bindings.",
                    nameof(roleBindings));
        }

        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<DocumentTemplateKind> CopyDocumentKinds(
        IReadOnlyList<DocumentTemplateKind> documentKinds)
    {
        ArgumentNullException.ThrowIfNull(documentKinds);

        var copy = new List<DocumentTemplateKind>(documentKinds.Count);

        foreach (var kind in documentKinds)
        {
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(documentKinds), kind, "A defined document template kind is required.");
            if (!copy.Contains(kind))
            {
                copy.Add(kind);
            }
        }

        return copy.AsReadOnly();
    }
}
