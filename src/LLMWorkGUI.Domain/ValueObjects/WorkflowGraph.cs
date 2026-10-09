using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

public sealed class WorkflowGraph
{
    private readonly Dictionary<string, WorkflowNodeDefinition> _nodesById;

    public WorkflowGraph(string entryNodeId, IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        EntryNodeId = DomainGuard.NotBlank(entryNodeId, nameof(entryNodeId));
        Nodes = CopyNodes(nodes);

        _nodesById = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);

        foreach (var node in Nodes)
        {
            if (!_nodesById.TryAdd(node.NodeId, node))
            {
                throw new InvalidOperationException(
                    $"The node '{node.NodeId}' is declared more than once.");
            }
        }
    }

    public string EntryNodeId { get; }

    public IReadOnlyList<WorkflowNodeDefinition> Nodes { get; }

    public WorkflowNodeDefinition? FindNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return _nodesById.GetValueOrDefault(nodeId);
    }

    public WorkflowNodeDefinition GetRequiredNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return FindNode(nodeId)
            ?? throw new InvalidOperationException(
                $"The node '{nodeId}' is not declared by the workflow graph.");
    }

    public void Validate()
    {
        if (!_nodesById.ContainsKey(EntryNodeId))
        {
            throw new InvalidOperationException(
                $"The entry node '{EntryNodeId}' is not declared by the workflow graph.");
        }

        foreach (var node in Nodes)
        {
            EnsureKindRequirements(node);
        }

        EnsureEveryNodeIsReachableFromTheEntry();

        foreach (var node in Nodes)
        {
            if (!node.HasOutgoingTransitions && node.Kind != WorkflowNodeKind.TerminalOutcome)
            {
                throw new InvalidOperationException(
                    $"Node '{node.NodeId}' has no success or failure transition; only a terminal "
                    + "outcome node may be a dead end.");
            }
        }

        if (!IsTerminalOutcomeReachable())
        {
            throw new InvalidOperationException(
                "The workflow graph cannot reach a terminal outcome from its entry node.");
        }

        EnsureCyclesAreJustified();
    }

    private static IReadOnlyList<WorkflowNodeDefinition> CopyNodes(
        IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var copy = new WorkflowNodeDefinition[nodes.Count];

        for (var index = 0; index < nodes.Count; index++)
        {
            copy[index] = nodes[index]
                ?? throw new ArgumentException(
                    "A workflow graph cannot contain null nodes.",
                    nameof(nodes));
        }

        return Array.AsReadOnly(copy);
    }

    private void EnsureKindRequirements(WorkflowNodeDefinition node)
    {
        if (node.SuccessTargetNodeId is { } successTarget && !_nodesById.ContainsKey(successTarget))
        {
            throw new InvalidOperationException(
                $"Node '{node.NodeId}' declares an unknown success target '{successTarget}'.");
        }

        if (node.FailureTargetNodeId is { } failureTarget && !_nodesById.ContainsKey(failureTarget))
        {
            throw new InvalidOperationException(
                $"Node '{node.NodeId}' declares an unknown failure target '{failureTarget}'.");
        }

        if (node.Kind == WorkflowNodeKind.Retry && node.RetryBudget <= 0)
        {
            throw new InvalidOperationException(
                $"The retry node '{node.NodeId}' must declare a positive retry budget.");
        }

        if (node.Kind == WorkflowNodeKind.Condition && node.ConditionExpression is null)
        {
            throw new InvalidOperationException(
                $"The condition node '{node.NodeId}' must declare a condition expression.");
        }

        if (node.Kind == WorkflowNodeKind.TerminalOutcome && node.HasOutgoingTransitions)
        {
            throw new InvalidOperationException(
                $"The terminal outcome node '{node.NodeId}' cannot declare outgoing transitions.");
        }
    }

    private void EnsureEveryNodeIsReachableFromTheEntry()
    {
        var visited = CollectReachableNodes();

        foreach (var node in Nodes)
        {
            if (!visited.Contains(node.NodeId))
            {
                throw new InvalidOperationException(
                    $"Node '{node.NodeId}' is not reachable from the entry node '{EntryNodeId}'.");
            }
        }
    }

    private HashSet<string> CollectReachableNodes()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        pending.Enqueue(EntryNodeId);

        while (pending.Count > 0)
        {
            var nodeId = pending.Dequeue();

            if (!visited.Add(nodeId))
            {
                continue;
            }

            var node = _nodesById[nodeId];

            if (node.SuccessTargetNodeId is { } successTarget)
            {
                pending.Enqueue(successTarget);
            }

            if (node.FailureTargetNodeId is { } failureTarget)
            {
                pending.Enqueue(failureTarget);
            }
        }

        return visited;
    }

    private bool IsTerminalOutcomeReachable() =>
        CollectReachableNodes()
            .Any(nodeId => _nodesById[nodeId].Kind == WorkflowNodeKind.TerminalOutcome);

    private void EnsureCyclesAreJustified()
    {
        // Only a successful Retry transition consumes budget. Remove those edges,
        // then reject every remaining cycle, including paths that bypass a Retry
        // in the same component or return through its exhausted failure branch.
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var node in Nodes)
        {
            VisitUnbudgetedTransitions(node.NodeId, states, path);
        }
    }

    private void VisitUnbudgetedTransitions(
        string nodeId,
        Dictionary<string, int> states,
        List<string> path)
    {
        if (states.TryGetValue(nodeId, out var state))
        {
            if (state == 1)
            {
                var cycle = path.Skip(path.IndexOf(nodeId))
                    .OrderBy(id => id, StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"The workflow graph contains an unjustified cycle across nodes: {string.Join(", ", cycle)}. "
                    + "Every cycle must traverse a successful Retry transition with a positive declared retry budget.");
            }
            return;
        }

        states[nodeId] = 1;
        path.Add(nodeId);
        foreach (var target in EnumerateUnbudgetedTargets(_nodesById[nodeId]))
        {
            VisitUnbudgetedTransitions(target, states, path);
        }
        path.RemoveAt(path.Count - 1);
        states[nodeId] = 2;
    }

    private static IEnumerable<string> EnumerateUnbudgetedTargets(WorkflowNodeDefinition node)
    {
        if (node.SuccessTargetNodeId is { } successTarget
            && !(node.Kind == WorkflowNodeKind.Retry && node.RetryBudget > 0))
        {
            yield return successTarget;
        }

        if (node.FailureTargetNodeId is { } failureTarget)
        {
            yield return failureTarget;
        }
    }
}
