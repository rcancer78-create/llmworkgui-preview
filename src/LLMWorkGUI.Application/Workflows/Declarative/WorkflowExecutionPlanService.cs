using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowExecutionPlanService : IWorkflowExecutionPlanService
{
    public WorkflowExecutionPlan CreatePlan(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        graph.Validate();

        return new WorkflowExecutionPlan(graph);
    }

    public WorkflowExecutionPlan AdvanceSuccess(
        WorkflowExecutionPlan plan,
        IReadOnlyList<CodingStageTransitionRule>? codingRules = null,
        CodingStageTransitionEvidence? evidence = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.IsComplete)
        {
            throw new WorkflowValidationException(
                $"The plan is already at the terminal outcome '{plan.CurrentNodeId}'.");
        }

        var codingRule = FindCodingRule(plan.CurrentNodeId, codingRules);

        if (codingRule is not null)
        {
            if (evidence is null)
            {
                throw new WorkflowValidationException(
                    $"Stage '{plan.CurrentNodeId}' declares coding transition rules; the transition "
                    + "evidence is required and the transition is blocked without it.");
            }

            var decision = codingRule.Evaluate(evidence);

            if (!decision.IsAllowed)
            {
                if (decision.RequiresEscalation && decision.EscalationTargetNodeId is not null)
                {
                    plan.MoveTo(
                        decision.EscalationTargetNodeId,
                        $"Forced escalation: {string.Join(" ", decision.BlockingReasons)}");

                    return plan;
                }

                throw new WorkflowValidationException(
                    $"The transition from stage '{plan.CurrentNodeId}' is blocked: "
                    + string.Join(" ", decision.BlockingReasons));
            }
        }

        var nextNodeId = plan.CurrentNode.SuccessTargetNodeId
            ?? throw new WorkflowValidationException(
                $"Node '{plan.CurrentNodeId}' declares no success transition.");

        plan.MoveTo(
            nextNodeId,
            reason ?? $"Success transition from '{plan.CurrentNodeId}'.");

        return plan;
    }

    public WorkflowExecutionPlan AdvanceFailure(WorkflowExecutionPlan plan, string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var guardedReason = ApplicationGuard.NotBlank(reason, nameof(reason));

        if (plan.IsComplete)
        {
            throw new WorkflowValidationException(
                $"The plan is already at the terminal outcome '{plan.CurrentNodeId}'.");
        }

        var nextNodeId = plan.CurrentNode.FailureTargetNodeId
            ?? throw new WorkflowValidationException(
                $"Node '{plan.CurrentNodeId}' declares no failure transition.");

        plan.MoveTo(nextNodeId, guardedReason);

        return plan;
    }

    private static CodingStageTransitionRule? FindCodingRule(
        string stageId,
        IReadOnlyList<CodingStageTransitionRule>? codingRules)
    {
        if (codingRules is null)
        {
            return null;
        }

        foreach (var rule in codingRules)
        {
            if (rule is not null && string.Equals(rule.StageId, stageId, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return null;
    }
}
