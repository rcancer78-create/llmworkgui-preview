using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

public sealed class WorkflowUnboundedCycleReviewTests
{
    [Theory]
    [InlineData(WorkflowNodeKind.Condition, 0)]
    [InlineData(WorkflowNodeKind.Escalation, 0)]
    [InlineData(WorkflowNodeKind.Condition, 2)]
    [InlineData(WorkflowNodeKind.Escalation, 2)]
    public void NonConsumingControlNodeBudgetDoesNotAuthorizeACycle(WorkflowNodeKind kind, int budget)
    {
        Assert.Throws<InvalidOperationException>(() => CreateGraph(kind, budget).Validate());
    }

    [Fact]
    public void PositiveRetryBudgetWithSuccessLoopAndTerminalExhaustionIsAccepted()
    {
        CreateGraph(WorkflowNodeKind.Retry, 2).Validate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetryInTheSameComponentDoesNotAuthorizeABypassCycle(bool selfLoop)
    {
        var nodes = new List<WorkflowNodeDefinition>
        {
            new WorkflowNodeDefinition("condition", WorkflowNodeKind.Condition, "Condition", "Coordinator",
                successTargetNodeId: selfLoop ? "condition" : "prompt", failureTargetNodeId: "retry",
                conditionExpression: "testsPassed"),
            new WorkflowNodeDefinition("retry", WorkflowNodeKind.Retry, "Retry", "Coordinator", retryBudget: 2,
                successTargetNodeId: "condition", failureTargetNodeId: "terminal"),
            new WorkflowNodeDefinition("terminal", WorkflowNodeKind.TerminalOutcome, "Terminal", "Coordinator")
        };
        if (!selfLoop)
        {
            nodes.Add(new WorkflowNodeDefinition("prompt", WorkflowNodeKind.Prompt, "Prompt", "Coordinator",
                successTargetNodeId: "condition"));
        }
        var graph = new WorkflowGraph("condition", nodes);

        Assert.Throws<InvalidOperationException>(graph.Validate);
    }

    [Fact]
    public void ExhaustedRetryFailureLoopDoesNotConsumeBudget()
    {
        var graph = new WorkflowGraph("retry",
        [
            new WorkflowNodeDefinition("retry", WorkflowNodeKind.Retry, "Retry", "Coordinator", retryBudget: 2,
                successTargetNodeId: "terminal", failureTargetNodeId: "retry"),
            new WorkflowNodeDefinition("terminal", WorkflowNodeKind.TerminalOutcome, "Terminal", "Coordinator")
        ]);

        Assert.Throws<InvalidOperationException>(graph.Validate);
    }

    [Fact]
    public void EachAlternativeLoopTraversingAConsumingRetrySuccessIsAccepted()
    {
        var graph = new WorkflowGraph("condition",
        [
            new WorkflowNodeDefinition("condition", WorkflowNodeKind.Condition, "Condition", "Coordinator",
                successTargetNodeId: "retry-one", failureTargetNodeId: "retry-two", conditionExpression: "testsPassed"),
            new WorkflowNodeDefinition("retry-one", WorkflowNodeKind.Retry, "First Retry", "Coordinator", retryBudget: 2,
                successTargetNodeId: "condition", failureTargetNodeId: "terminal"),
            new WorkflowNodeDefinition("retry-two", WorkflowNodeKind.Retry, "Second Retry", "Coordinator", retryBudget: 3,
                successTargetNodeId: "condition", failureTargetNodeId: "terminal"),
            new WorkflowNodeDefinition("terminal", WorkflowNodeKind.TerminalOutcome, "Terminal", "Coordinator")
        ]);

        graph.Validate();
    }

    private static WorkflowGraph CreateGraph(WorkflowNodeKind kind, int budget) => new("control",
    [
        new WorkflowNodeDefinition("control", kind, "Control", "Coordinator", retryBudget: budget,
            successTargetNodeId: "control", failureTargetNodeId: "terminal",
            conditionExpression: kind == WorkflowNodeKind.Condition ? "testsPassed" : null),
        new WorkflowNodeDefinition("terminal", WorkflowNodeKind.TerminalOutcome, "Terminal", "Coordinator")
    ]);
}
