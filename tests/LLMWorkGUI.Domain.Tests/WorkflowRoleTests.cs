using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class WorkflowRoleTests
{
    [Fact]
    public void WorkflowRole_ExposesNormativeMembersInOrder()
    {
        Assert.Equal(
            new[] { "Coordinator", "Executor", "Reviewer", "Escalation", "Unknown" },
            Enum.GetNames<WorkflowRole>());
    }

    [Theory]
    [InlineData(WorkflowRole.Coordinator)]
    [InlineData(WorkflowRole.Executor)]
    [InlineData(WorkflowRole.Reviewer)]
    [InlineData(WorkflowRole.Escalation)]
    [InlineData(WorkflowRole.Unknown)]
    public void WorkflowRole_DefinesEveryMember(WorkflowRole role)
    {
        Assert.True(Enum.IsDefined(role));
    }

    [Fact]
    public void WorkflowRole_DefaultValueIsCoordinator()
    {
        Assert.Equal(WorkflowRole.Coordinator, default);
    }
}
