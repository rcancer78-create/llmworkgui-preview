using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>The outcome of validating an editable template graph before it is saved or previewed.</summary>
public sealed record WorkflowGraphValidationReport(bool IsValid, IReadOnlyList<string> Errors)
{
    public static WorkflowGraphValidationReport Valid { get; } = new(true, Array.Empty<string>());

    public static WorkflowGraphValidationReport Invalid(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new WorkflowGraphValidationReport(false, errors.ToArray());
    }

    public string Summary => IsValid
        ? "The edited template graph is valid and can be previewed before a run."
        : $"The edited template graph is invalid: {string.Join("; ", Errors)}";
}

/// <summary>Result of assigning a template version to a project. It only moves an assignment pointer; the
/// active run, the template graph and the imported workflow ZIP are never written.</summary>
public sealed record WorkflowTemplateAssignmentResult(
    bool IsAssigned,
    string ProjectId,
    string TemplateId,
    int TemplateVersion,
    string? AssignmentId,
    string? ActiveRunId,
    string? ActiveRunStageId,
    IReadOnlyList<string> ImportedPackageHashes,
    string? Blocker)
{
    public string Summary => IsAssigned
        ? $"Assigned template '{TemplateId}' v{TemplateVersion} to project '{ProjectId}'. "
            + "The active run and the imported ZIP were not modified."
        : $"Assigning template '{TemplateId}' v{TemplateVersion} was blocked: {Blocker}";
}

/// <summary>
/// Workflow Studio: create, save, clone and version workflow templates, validate and preview the graph
/// before a run, assign a template version to a project without mutating the active run or the imported
/// ZIP, and request an AGY/Codex account-context switch that only reports success on independently
/// reported evidence (ROADMAP Phase 10E).
/// </summary>
public interface IWorkflowStudioService
{
    /// <summary>The shipped templates. Built-in versions are immutable and are never overwritten.</summary>
    IReadOnlyList<WorkflowTemplateDefinition> GetBuiltInTemplates();

    /// <summary>The reviewer roles every required document must collect before the pre-coder gate.</summary>
    IReadOnlyList<string> DocumentReviewerRoles { get; }

    WorkflowGraphValidationReport ValidateTemplateGraph(WorkflowGraph graph);

    Task<IReadOnlyList<WorkflowTemplateDefinition>> ListTemplatesAsync(
        CancellationToken cancellationToken = default);

    Task<WorkflowTemplateDefinition> GetRequiredTemplateAsync(
        string templateId,
        int? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a new immutable template version. Existing versions are never overwritten.</summary>
    Task<WorkflowTemplateDefinition> SaveTemplateAsync(
        WorkflowTemplateDefinition template,
        CancellationToken cancellationToken = default);

    /// <summary>Clones and saves a user-owned template at version 1 before returning.</summary>
    Task<WorkflowTemplateDefinition> CloneTemplateAsync(
        string templateId,
        string newTemplateId,
        string newDisplayName,
        int? sourceVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates and saves a new user-owned version without mutating the source version.</summary>
    Task<WorkflowTemplateDefinition> CreateTemplateVersionAsync(
        string templateId,
        int newVersion,
        int? sourceVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Assigns a template version to a project. Active runs and imported packages are read-only.</summary>
    Task<WorkflowTemplateAssignmentResult> AssignTemplateToProjectAsync(
        string projectId,
        string templateId,
        int templateVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the AGY <c>agy-profile</c> or Codex <c>CODEX_HOME</c> context switch. The previous
    /// session, its credentials and the Mirasim state are never carried over.
    ///
    /// A switch is only reported as applied when a backend independently reported the account, the
    /// actual model, a unique route key and the new native session. Without that evidence the request is
    /// refused before any process, gateway, session, execution or role binding is touched, and the caller
    /// may not supply the missing observation itself.
    /// </summary>
    Task<WorkflowAccountContextSwitchResult> SwitchAccountContextAsync(
        WorkflowAccountContextSwitchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Helpers shared by the Studio UI and tests.</summary>
public static class WorkflowStudioDocumentRules
{
    /// <summary>Roles that must unanimously approve every required document before coding may start.</summary>
    public static IReadOnlyList<string> RequiredReviewerRoles { get; } =
        Array.AsReadOnly(new[] { "Reviewer", "Architect", "UiReviewer" });

    public static IReadOnlyList<DocumentTemplateKind> RequiredDocumentKinds { get; } =
        Array.AsReadOnly(new[]
        {
            DocumentTemplateKind.ProblemStatement,
            DocumentTemplateKind.Architecture,
            DocumentTemplateKind.TechnicalSpecification,
            DocumentTemplateKind.Roadmap,
            DocumentTemplateKind.TaskPacket,
            DocumentTemplateKind.ReviewReport,
            DocumentTemplateKind.AcceptanceReport
        });
}
