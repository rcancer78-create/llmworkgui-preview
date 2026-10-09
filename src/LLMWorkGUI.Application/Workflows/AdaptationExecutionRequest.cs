using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationExecutionRequest
{
    public AdaptationExecutionRequest(
        string workflowVersionId,
        string adapterRouteId,
        AdaptationGoal goal,
        bool allowExpandedSemanticScope = false,
        IReadOnlyList<string>? userExcludedFiles = null)
    {
        WorkflowVersionId = ApplicationGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));
        AdapterRouteId = ApplicationGuard.NotBlank(adapterRouteId, nameof(adapterRouteId));
        Goal = goal;
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
        UserExcludedFiles = (userExcludedFiles ?? Array.Empty<string>()).ToArray();
    }

    public string WorkflowVersionId { get; }

    public string AdapterRouteId { get; }

    public AdaptationGoal Goal { get; }

    /// <summary>
    /// The pre-send expanded-scope flag. It is prompt context for the adapter and nothing else: it never
    /// suppresses a detected semantic change and never becomes a recorded approval, so an operator decision
    /// on the exact reported issues is still required.
    /// </summary>
    public bool AllowExpandedSemanticScope { get; }

    public IReadOnlyList<string> UserExcludedFiles { get; }

    /// <summary>The selected stored project whose current data policy applies to this adaptation.</summary>
    public string? ProjectId { get; init; }
}
