using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowStudioTests
{
    [Theory]
    [InlineData(WorkflowScheme.MultiLevelReviewStageId)]
    [InlineData(WorkflowScheme.TestsAndUiAcceptanceStageId)]
    public void LegacyBuiltInBackwardReworkEdgesWithWriterBudgetAreRejected(string failureStageId)
    {
        var canonical = WorkflowStudioService.CreateStandardTemplate();
        canonical.Graph.Validate();
        var legacy = new WorkflowGraph(canonical.Graph.EntryNodeId,
            canonical.Graph.Nodes.Select(node => new WorkflowNodeDefinition(
                node.NodeId, node.Kind, node.DisplayName, node.RoleBinding,
                node.RequiredCapabilities, node.PrimaryRouteId, node.FallbackRouteIds, node.Timeout,
                node.NodeId == WorkflowScheme.CodeAndUiStageId ? 1 : node.RetryBudget,
                node.SuccessTargetNodeId,
                node.NodeId == failureStageId ? WorkflowScheme.CodeAndUiStageId : node.FailureTargetNodeId,
                node.ConditionExpression, node.ArtifactContract, node.PermissionIntent, node.GateMetadata)).ToArray());

        Assert.Throws<InvalidOperationException>(legacy.Validate);
    }
}
