namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// Everything a run needs in order to execute an assigned template version without reading the template
/// store again: which version was assigned, the exact graph it held and the scheme derived from it.
/// </summary>
public sealed record WorkflowTemplateExecutionPlan(
    string TemplateId,
    int TemplateVersion,
    string GraphSnapshotJson,
    string SchemeSnapshotJson,
    WorkflowSchemeSnapshot Scheme);
