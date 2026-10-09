using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowPlanningReviewCorrectionsTests
{
    [Fact]
    public async Task FailureHandlerSuccessContinuationIsRetainedAsDeclaredMetadata()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var graph = new WorkflowGraph("start", new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeKind.Prompt, "Start", "Worker",
                successTargetNodeId: "done", failureTargetNodeId: "handler"),
            new WorkflowNodeDefinition("done", WorkflowNodeKind.TerminalOutcome, "Done", "Worker"),
            new WorkflowNodeDefinition("handler", WorkflowNodeKind.Prompt, "Handler", "Worker",
                successTargetNodeId: "handler-done"),
            new WorkflowNodeDefinition("handler-done", WorkflowNodeKind.TerminalOutcome, "Handler done", "Worker")
        });
        graph.Validate(); // The continuation is reachable through real declared graph edges.
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(new WorkflowTemplateDefinition("template", 1, "Template", "Failure metadata",
            graph, Array.Empty<RoleBindingDefinition>(), Array.Empty<DocumentTemplateKind>(), false, now));
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment("assignment", "project", "template", 1, now));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync("project");

        var restored = WorkflowSchemeSnapshot.Deserialize(plan.SchemeSnapshotJson, "run").Scheme;
        Assert.Equal(new[] { "start", "done", "handler", "handler-done" }, restored.Stages.Select(stage => stage.StageId));
        Assert.Equal("handler", restored.GetRequiredStage("start").FailureStageId);
        Assert.Equal("handler-done", restored.GetRequiredStage("handler").NextStageId);
        // Keeping metadata is not permission to execute an unsupported automatic failure transition.
    }
}
