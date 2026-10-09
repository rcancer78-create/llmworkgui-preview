using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowExecutionPlan
{
    private readonly List<WorkflowExecutionPlanStep> _steps;

    internal WorkflowExecutionPlan(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        Graph = graph;
        CurrentNodeId = graph.EntryNodeId;
        _steps = new List<WorkflowExecutionPlanStep>
        {
            new(
                graph.EntryNodeId,
                graph.GetRequiredNode(graph.EntryNodeId).Kind,
                NextNodeId: null,
                "The plan starts at the entry node.")
        };
    }

    public WorkflowGraph Graph { get; }

    public string CurrentNodeId { get; private set; }

    public WorkflowNodeDefinition CurrentNode => Graph.GetRequiredNode(CurrentNodeId);

    public IReadOnlyList<WorkflowExecutionPlanStep> Steps => _steps;

    public bool IsComplete => CurrentNode.Kind == WorkflowNodeKind.TerminalOutcome;

    internal void MoveTo(string nodeId, string reason)
    {
        var target = Graph.GetRequiredNode(nodeId);

        _steps.Add(new WorkflowExecutionPlanStep(target.NodeId, target.Kind, null, reason));
        CurrentNodeId = target.NodeId;
    }
}

public sealed record WorkflowExecutionPlanStep(
    string NodeId,
    WorkflowNodeKind Kind,
    string? NextNodeId,
    string Reason);
