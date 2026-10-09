using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public interface IWorkflowExecutionPlanService
{
    WorkflowExecutionPlan CreatePlan(WorkflowGraph graph);

    WorkflowExecutionPlan AdvanceSuccess(
        WorkflowExecutionPlan plan,
        IReadOnlyList<CodingStageTransitionRule>? codingRules = null,
        CodingStageTransitionEvidence? evidence = null,
        string? reason = null);

    WorkflowExecutionPlan AdvanceFailure(WorkflowExecutionPlan plan, string reason);
}
