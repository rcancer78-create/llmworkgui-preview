using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class DeclarativeNodeExecutionTests
{
    [Fact]
    public async Task RetryNode_CannotInflateItsDeclaredBudgetThroughTheRequest()
    {
        var node = Node("retry", WorkflowNodeKind.Retry, success: "writer", failure: "escalation", retryBudget: 2);
        var result = await CreateExecutor().ExecuteAsync(CreateRequest(node,
            new RoleBindingDefinition("Coder", "route-opencode"), previousExecutionId: "previous",
            previousExecutionState: ExecutionState.Failed, retryBudgetRemaining: 100));
        Assert.Equal(1, result.RemainingRetryBudget);
        Assert.Equal("writer", result.NextNodeId);
        Assert.Empty(_channels);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Cancelled)]
    [InlineData((ExecutionState)999)]
    public async Task RetryNode_RefusesANonFailureWithoutSpendingBudget(ExecutionState? previousState)
    {
        var node = Node("retry", WorkflowNodeKind.Retry, success: "writer", failure: "escalation", retryBudget: 2);
        var request = CreateRequest(node, new RoleBindingDefinition("Coder", "route-opencode"),
            previousExecutionId: "previous", previousExecutionState: previousState, retryBudgetRemaining: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateExecutor().ExecuteAsync(request));
        Assert.Empty(_channels);
    }
}
