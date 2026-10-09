using System.Globalization;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// Turns the template version assigned to a project into the exact plan a run is pinned to.
///
/// This is deliberately a narrow slice. It accepts a chain graph - one entry node, every other node
/// reached exactly once along a single success chain, optionally reached again as the declared failure
/// target of a node that has one - with no condition, fallback route or permission intent. Only an
/// ArtifactCollection node may declare an artifact contract, exactly matching its preserved artifact gate.
/// Other unsupported graphs are refused with a named blocker instead of being approximated: mapping a
/// user-decision, escalation, condition or retry node would silently drop that node's safety semantics.
///
/// The shipped built-in <c>workflow-standard-development-bounded@1</c> is inside that slice, and is accepted on a
/// stricter footing than any other template: its stored graph is proved to be exactly the graph
/// <see cref="WorkflowStudioService.CreateStandardTemplate"/> produces before a single stage is derived. A
/// built-in graph that was changed or corrupted is refused by name, and the process-wide
/// <see cref="WorkflowScheme.CreateStandardDevelopmentScheme"/> is never substituted for the graph just
/// because a template carries the built-in id.
///
/// The derived scheme maps each node one-to-one and adds nothing: identical id, display name and role
/// binding, the failure target as the failure stage, and the success target as the next stage. No reviewer
/// and no approval is ever inferred from a role name or from a node kind.
///
/// A node's preserved <see cref="WorkflowNodeDefinition.GateMetadata"/> is read for exactly this reason, and
/// its four fields become the four gate fields of the derived stage verbatim: the same
/// <see cref="WorkflowStageKind"/>, the same required reviewer roles in their declared order, the same user
/// approval flag and the same artifact requirement. Absent metadata and the explicit no-gate tuple both keep
/// the gate-free mapping, because a document that never declared a gate is not evidence of one that was
/// then dropped - while a declared gate is carried into the scheme or refused, never weakened.
///
/// A node kind is a label and never evidence that anything ran. The four kinds the shipped built-in graph
/// uses - <see cref="WorkflowNodeKind.Review"/>, <see cref="WorkflowNodeKind.ApprovalGate"/>,
/// <see cref="WorkflowNodeKind.Writer"/> and <see cref="WorkflowNodeKind.ValidationCommand"/> - are mapped
/// onto the same stage-gate semantics a prompt node already has, and only when the node's own declaration
/// says what the kind names: a review node that declares no reviewer, an approval-gate node that declares
/// no approval, a writer or validation node that declares no stored artifact, are all refused rather than
/// executed as a gate-free stage wearing a label that promises a gate.
///
/// A declared gate is refused, by name, when no run could enforce it as written: a required reviewer or a
/// user approval with no artifact requirement has no stored content to decide on, and a terminal outcome
/// node has no transition to evaluate a gate at all. Both refusals happen before a run row exists.
///
/// A failure target is preserved - on the derived stage, on the pinned scheme and on the pinned graph - and
/// is never followed by a transition this slice performs. A retry budget is preserved on the pinned graph of
/// a non-terminal node and is never read as a stage-transition retry or an automatic retry; node execution
/// is unavailable, so the declaration is kept for the executor that will own it rather than being turned
/// into behaviour nothing here implements.
/// </summary>
public sealed class WorkflowTemplateExecutionPlanResolver
{
    /// <summary>The node kinds this slice maps onto a scheme stage.</summary>
    private const string SupportedNodeKinds =
        "'Prompt', 'Review', 'Writer', 'ApprovalGate', 'ValidationCommand', 'ArtifactCollection' and 'TerminalOutcome'";

    private readonly IWorkflowTemplateStore _templateStore;

    public WorkflowTemplateExecutionPlanResolver(IWorkflowTemplateStore templateStore)
    {
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
    }

    /// <summary>
    /// Resolves the assigned template version of a project into its pinned plan, or refuses the project
    /// with a named blocker. There is no fallback: a missing assignment, a version that no longer exists
    /// and a graph this slice cannot execute faithfully all leave the caller without a plan.
    /// </summary>
    public async Task<WorkflowTemplateExecutionPlan> ResolveForProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var guardedProjectId = ApplicationGuard.NotBlank(projectId, nameof(projectId));

        var assignment = await _templateStore
            .GetAssignmentAsync(guardedProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (assignment is null)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.MissingAssignment,
                $"Project '{guardedProjectId}' has no assigned workflow template version, so there is no "
                    + "template to pin the run to.");
        }

        var context = string.Create(
            CultureInfo.InvariantCulture,
            $"project '{guardedProjectId}' assigned template '{assignment.TemplateId}' "
                + $"version {assignment.TemplateVersion}");

        var template = await _templateStore
            .GetAsync(assignment.TemplateId, assignment.TemplateVersion, cancellationToken)
            .ConfigureAwait(false);

        if (template is null)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.MissingTemplateVersion,
                $"The {context} is not present in the template store.");
        }

        // The shipped built-in is proved before anything is derived from it, so a changed or corrupted
        // built-in graph reports its own difference instead of a downstream symptom of one.
        EnsureBuiltInStandardTemplateIsUnchanged(template, context);

        EnsureNodesAreRepresentable(template.Graph, context);

        var graph = template.Graph;
        var executionOrder = ResolveExecutionOrder(graph, context);

        EnsureGraphIsValid(graph, context);

        var stages = executionOrder
            .Select(DeriveStage)
            .ToArray();

        var scheme = CreateScheme(graph, stages, context);

        return new WorkflowTemplateExecutionPlan(
            template.TemplateId,
            template.Version,
            WorkflowGraphSnapshot.Serialize(graph),
            scheme.Json,
            scheme);
    }

    /// <summary>
    /// Proves the stored graph of the shipped built-in template is exactly the graph the studio produces,
    /// or refuses it by name.
    ///
    /// The built-in is the one template every default project ends up on, so a store row that was edited,
    /// truncated or restored from an older build must not become the plan a run is pinned to. The proof is
    /// structural and total - entry node, node set, and every field of every node including its four gate
    /// fields - and the first difference it finds is named back, because "the built-in template changed" is
    /// only actionable if it says what changed.
    ///
    /// The shipped id/version pair remains protected even if its stored built-in flag is changed.
    /// Later user-owned versions of that id use the ordinary graph validation and derivation instead;
    /// neither path substitutes the process-wide standard scheme for the stored graph.
    /// </summary>
    private static void EnsureBuiltInStandardTemplateIsUnchanged(
        WorkflowTemplateDefinition template,
        string context)
    {
        if (!string.Equals(
                template.TemplateId,
                WorkflowStudioService.StandardTemplateId,
                StringComparison.Ordinal)
            || template.Version != WorkflowStudioService.StandardTemplateVersion)
        {
            return;
        }

        var canonical = WorkflowStudioService.CreateStandardTemplate().Graph;
        var difference = DescribeFirstGraphDifference(canonical, template.Graph);

        if (difference is null)
        {
            return;
        }

        throw Blocked(
            WorkflowTemplateExecutionBlockers.BuiltInStandardTemplateGraphChanged,
            $"The {context} is the shipped built-in template, but its stored graph is not the graph the "
                + $"studio produces: {difference}. The built-in is pinned exactly as shipped or not at all, "
                + "and it is never rebuilt from the process-wide standard scheme, so the run is refused "
                + "before a run row is inserted.");
    }

    /// <summary>
    /// The first structural difference between two graphs, or null when they are the same graph. Every field
    /// a node carries is compared, in a fixed order, so the message names one concrete difference rather
    /// than a summary nobody can act on.
    /// </summary>
    private static string? DescribeFirstGraphDifference(WorkflowGraph expected, WorkflowGraph actual)
    {
        if (!string.Equals(expected.EntryNodeId, actual.EntryNodeId, StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"its entry node is '{actual.EntryNodeId}' instead of '{expected.EntryNodeId}'");
        }

        if (expected.Nodes.Count != actual.Nodes.Count)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"it declares {actual.Nodes.Count} node(s) instead of {expected.Nodes.Count}");
        }

        foreach (var expectedNode in expected.Nodes)
        {
            var actualNode = actual.FindNode(expectedNode.NodeId);

            if (actualNode is null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"node '{expectedNode.NodeId}' is missing");
            }

            var difference = DescribeFirstNodeDifference(expectedNode, actualNode);

            if (difference is not null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"node '{expectedNode.NodeId}' {difference}");
            }
        }

        return null;
    }

    private static string? DescribeFirstNodeDifference(
        WorkflowNodeDefinition expected,
        WorkflowNodeDefinition actual)
    {
        if (expected.Kind != actual.Kind)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"is a '{actual.Kind}' node instead of a '{expected.Kind}' node");
        }

        if (!string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"is named '{actual.DisplayName}' instead of '{expected.DisplayName}'");
        }

        if (!string.Equals(expected.RoleBinding, actual.RoleBinding, StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"binds role '{actual.RoleBinding}' instead of '{expected.RoleBinding}'");
        }

        if (!string.Equals(expected.PrimaryRouteId, actual.PrimaryRouteId, StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"declares primary route '{actual.PrimaryRouteId ?? "<none>"}' instead of "
                    + $"'{expected.PrimaryRouteId ?? "<none>"}'");
        }

        if (!expected.RequiredCapabilities.SequenceEqual(actual.RequiredCapabilities, StringComparer.Ordinal))
        {
            return "declares different required capabilities";
        }

        if (expected.Timeout != actual.Timeout)
        {
            return "declares a different timeout";
        }

        if (expected.RetryBudget != actual.RetryBudget)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"declares a retry budget of {actual.RetryBudget} instead of {expected.RetryBudget}");
        }

        if (!string.Equals(
                expected.SuccessTargetNodeId,
                actual.SuccessTargetNodeId,
                StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"declares success target '{actual.SuccessTargetNodeId ?? "<none>"}' instead of "
                    + $"'{expected.SuccessTargetNodeId ?? "<none>"}'");
        }

        if (!string.Equals(
                expected.FailureTargetNodeId,
                actual.FailureTargetNodeId,
                StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"declares failure target '{actual.FailureTargetNodeId ?? "<none>"}' instead of "
                    + $"'{expected.FailureTargetNodeId ?? "<none>"}'");
        }

        if (!string.Equals(
                expected.ConditionExpression,
                actual.ConditionExpression,
                StringComparison.Ordinal))
        {
            return "declares a different condition expression";
        }

        if (!expected.FallbackRouteIds.SequenceEqual(actual.FallbackRouteIds, StringComparer.Ordinal))
        {
            return "declares different fallback routes";
        }

        if (!string.Equals(expected.ArtifactContract, actual.ArtifactContract, StringComparison.Ordinal))
        {
            return "declares a different artifact contract";
        }

        if (!string.Equals(expected.PermissionIntent, actual.PermissionIntent, StringComparison.Ordinal))
        {
            return "declares a different permission intent";
        }

        return expected.GateMetadata is { } expectedGate
            ? DescribeGateDifference(expectedGate, actual.GateMetadata)
            : actual.GateMetadata is null
                ? null
                : "declares stage gates where the shipped node declares none";
    }

    private static string? DescribeGateDifference(
        WorkflowNodeGateMetadata expected,
        WorkflowNodeGateMetadata? actual)
    {
        if (actual is null)
        {
            return "declares no stage gates where the shipped node declares them";
        }

        if (expected.StageKind != actual.StageKind)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"declares stage kind '{actual.StageKind}' instead of '{expected.StageKind}'");
        }

        if (!expected.RequiredReviewerRoles.SequenceEqual(actual.RequiredReviewerRoles, StringComparer.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"requires reviewer(s) [{string.Join(", ", actual.RequiredReviewerRoles)}] instead of "
                    + $"[{string.Join(", ", expected.RequiredReviewerRoles)}]");
        }

        if (expected.RequiresUserApproval != actual.RequiresUserApproval)
        {
            return actual.RequiresUserApproval
                ? "requires a user approval where the shipped node requires none"
                : "drops the user approval the shipped node requires";
        }

        if (!string.Equals(expected.ArtifactRequirement, actual.ArtifactRequirement, StringComparison.Ordinal))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"requires artifact '{actual.ArtifactRequirement ?? "<none>"}' instead of "
                    + $"'{expected.ArtifactRequirement ?? "<none>"}'");
        }

        return null;
    }

    /// <summary>
    /// The scheme the run is advanced against, or a named refusal. The explicit gate checks above already
    /// reject every tuple a run could not enforce; this is the backstop that keeps any remaining
    /// inconsistency a named blocker instead of an untyped scheme failure escaping <c>StartRunAsync</c>.
    /// </summary>
    private static WorkflowSchemeSnapshot CreateScheme(
        WorkflowGraph graph,
        IReadOnlyList<WorkflowStageDefinition> stages,
        string context)
    {
        try
        {
            return WorkflowSchemeSnapshot.CreateFrom(new WorkflowScheme(graph.EntryNodeId, stages));
        }
        catch (Exception exception) when (exception is WorkflowValidationException
            or InvalidOperationException
            or ArgumentException)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.UnrepresentableNodeGate,
                $"The stage gates declared by the {context} do not describe a scheme a run could execute: "
                    + exception.Message);
        }
    }

    private static void EnsureGraphIsValid(WorkflowGraph graph, string context)
    {
        try
        {
            graph.Validate();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.InvalidGraph,
                $"The {context} does not store a valid workflow graph: {exception.Message}");
        }
    }

    private static void EnsureNodesAreRepresentable(WorkflowGraph graph, string context)
    {
        // Three passes on purpose. The first asks whether this graph is inside the slice at all, and reports
        // the reason it is not - a node kind, a transition, a condition, an artifact contract. The second
        // asks whether a kind that does map agrees with what the node itself declared, which is a different
        // question from whether the graph is inside the slice: a review node that declares no reviewer is
        // inside the slice and still says something the derived stage cannot carry. Only once both hold
        // does the third pass read the preserved gates, which answers a third question: a graph that would
        // otherwise be executed still declares a gate no run could enforce as written. Interleaving them
        // would let a node that is refused for its kind anyway report its gate instead, and the graph-level
        // reason is the more useful one to name.
        foreach (var node in graph.Nodes)
        {
            EnsureNodeIsRepresentable(node, graph, context);
        }

        foreach (var node in graph.Nodes)
        {
            EnsureNodeKindAgreesWithItsDeclaredGate(node, context);
        }

        foreach (var node in graph.Nodes)
        {
            EnsureNodeGateIsEnforceable(node, context);
        }
    }

    private static void EnsureNodeIsRepresentable(
        WorkflowNodeDefinition node,
        WorkflowGraph graph,
        string context)
    {
        if (node.Kind is not (WorkflowNodeKind.Prompt
            or WorkflowNodeKind.Review
            or WorkflowNodeKind.Writer
            or WorkflowNodeKind.ApprovalGate
            or WorkflowNodeKind.ValidationCommand
            or WorkflowNodeKind.ArtifactCollection
            or WorkflowNodeKind.TerminalOutcome))
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
                $"The {context} declares node '{node.NodeId}' as '{node.Kind}', and this slice maps only "
                    + $"{SupportedNodeKinds} onto a scheme stage. The node's safety semantics are dropped "
                    + "by that mapping, so the template is refused instead of being executed without them.");
        }

        if (node.Kind == WorkflowNodeKind.TerminalOutcome)
        {
            if (node.HasOutgoingTransitions)
            {
                throw Blocked(
                    WorkflowTemplateExecutionBlockers.TerminalTransition,
                    $"The {context} declares a transition on its terminal outcome node '{node.NodeId}'.");
            }
        }
        else
        {
            if (node.FailureTargetNodeId is { } failureTargetNodeId
                && graph.FindNode(failureTargetNodeId) is null)
            {
                throw Blocked(
                    WorkflowTemplateExecutionBlockers.FailureTransition,
                    $"The {context} declares a failure target '{failureTargetNodeId}' on node "
                        + $"'{node.NodeId}', and that node is not declared by the graph, so the declared "
                        + "failure route names nothing this slice could preserve.");
            }

            if (node.SuccessTargetNodeId is null)
            {
                throw Blocked(
                    WorkflowTemplateExecutionBlockers.SuccessTransition,
                    $"The {context} declares node '{node.NodeId}' without exactly one success target.");
            }
        }

        // A retry budget belongs to node execution, which is unavailable here, so on a non-terminal node it
        // is preserved in the pinned graph and never read again by this service or by a transition. A
        // terminal outcome node performs no execution at all, so a budget declared there has no referent
        // and is refused instead of being preserved as a field nothing could ever read.
        if (node.RetryBudget != 0 && node.Kind == WorkflowNodeKind.TerminalOutcome)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.RetryBudget,
                $"The {context} declares a retry budget of {node.RetryBudget} on its terminal outcome node "
                    + $"'{node.NodeId}'. A retry budget describes node execution before a transition, and a "
                    + "terminal outcome node executes nothing, so there is nothing for it to retry.");
        }

        if (node.ConditionExpression is not null)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.Condition,
                $"The {context} declares a condition on node '{node.NodeId}'.");
        }

        if (node.FallbackRouteIds.Count > 0)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.FallbackRoute,
                $"The {context} declares {node.FallbackRouteIds.Count} fallback route(s) on node "
                    + $"'{node.NodeId}'.");
        }

        if (node.Kind == WorkflowNodeKind.ArtifactCollection)
        {
            if (node.ArtifactContract is null || node.GateMetadata?.ArtifactRequirement != node.ArtifactContract)
                throw Blocked(WorkflowTemplateExecutionBlockers.ArtifactContract,
                    $"The {context} collection node '{node.NodeId}' must declare one artifact kind matching its stage gate.");
        }
        else if (node.ArtifactContract is not null)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.ArtifactContract,
                $"The {context} declares an artifact contract on node '{node.NodeId}'.");
        }

        if (node.PermissionIntent is not null)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.PermissionIntent,
                $"The {context} declares a permission intent on node '{node.NodeId}'.");
        }
    }

    /// <summary>
    /// Refuses a node whose kind promises a gate its own declaration does not carry.
    ///
    /// A kind is a label. It never supplies a reviewer, an approval or an artifact requirement, and it is
    /// never evidence that a model ran: the four fields of the declared gate are the whole of the gate. But
    /// a label that names a gate and then declares none would be executed as a gate-free stage, so the one
    /// thing the label promises would be the one thing dropped - which is the loss this whole slice exists
    /// to prevent. A review node has to declare the reviewer whose verdict would let the stage pass, an
    /// approval-gate node has to declare the approval, and a writer or validation node has to declare the
    /// stored artifact its content is written to or checked against, because a stage with no artifact
    /// requirement is a stage no artifact, verdict or approval can ever authorize.
    ///
    /// A prompt node and a terminal outcome node ask for nothing, which is why a gate-free prompt chain and a
    /// stage-kind-only terminal node keep the mapping they always had.
    /// </summary>
    private static void EnsureNodeKindAgreesWithItsDeclaredGate(
        WorkflowNodeDefinition node,
        string context)
    {
        if (node.Kind == WorkflowNodeKind.TerminalOutcome)
        {
            return;
        }

        var gate = node.GateMetadata;
        var reviewerCount = gate?.RequiredReviewerRoles.Count ?? 0;
        var requiresUserApproval = gate?.RequiresUserApproval ?? false;
        var artifactRequirement = gate?.ArtifactRequirement;

        var missing = node.Kind switch
        {
            WorkflowNodeKind.Review when reviewerCount == 0
                => "no required reviewer role",
            WorkflowNodeKind.ApprovalGate when !requiresUserApproval
                => "no user approval",
            WorkflowNodeKind.Writer or WorkflowNodeKind.ValidationCommand
                when artifactRequirement is null
                => "no artifact requirement",
            _ => null
        };

        if (missing is null)
        {
            return;
        }

        throw Blocked(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            $"The {context} declares node '{node.NodeId}' as a '{node.Kind}' node with {missing}: "
                + $"{DescribeDeclaredGate(node)}. A node kind is a label and never supplies a gate, so the "
                + "label is refused here rather than executed as a stage that quietly dropped the gate it "
                + "names.");
    }

    private static string DescribeDeclaredGate(WorkflowNodeDefinition node) =>
        node.GateMetadata is { } gate
            ? DescribeGate(gate)
            : "the node declares no stage gates at all";

    /// <summary>
    /// The scheme stage a node maps onto, or the refusal of a gate no run could enforce.
    ///
    /// Absent metadata is a document that never declared a gate, and the explicit no-gate tuple is a
    /// document that says so; both keep the gate-free mapping. Present metadata that declares anything is
    /// copied field for field, so a stage kind, a reviewer list and its order, an approval flag and an
    /// artifact requirement all reach the scheme exactly as the node stated them. The four fields are never
    /// re-read from the kind or the role binding.
    ///
    /// The failure target is carried across as the stage's failure stage, so the declared route stays on the
    /// run and in its pinned scheme snapshot. Carrying it is not taking it: no transition in this slice
    /// follows a failure stage, so the edge is visible evidence of what the template declared and nothing
    /// more.
    /// </summary>
    private static WorkflowStageDefinition DeriveStage(WorkflowNodeDefinition node)
    {
        if (node.GateMetadata is not { } gate || gate.IsNoGate)
        {
            return new WorkflowStageDefinition(
                node.NodeId,
                node.DisplayName,
                node.RoleBinding,
                WorkflowStageKind.Custom,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null,
                nextStageId: node.SuccessTargetNodeId,
                failureStageId: node.FailureTargetNodeId);
        }

        return new WorkflowStageDefinition(
            node.NodeId,
            node.DisplayName,
            node.RoleBinding,
            gate.StageKind,
            gate.RequiredReviewerRoles,
            gate.RequiresUserApproval,
            gate.ArtifactRequirement,
            node.SuccessTargetNodeId,
            node.FailureTargetNodeId);
    }

    /// <summary>
    /// Refuses a declared gate that the run could never enforce as written.
    ///
    /// A terminal outcome node has no outgoing transition, so a reviewer, a user approval or an artifact
    /// requirement declared there is decided by nothing at all: the run reaches the node and completes. A
    /// stage-kind-only declaration is the exception and is representable - the stage kind names what the
    /// terminal stage is, and no gate is expected of it.
    ///
    /// On a non-terminal node the refusal is a human gate with no artifact requirement. The transition gate
    /// decides reviewer verdicts and an approval against the hash of a stored artifact, so without one there
    /// is no content to check and the only hash available would be the one a verdict happened to carry.
    /// </summary>
    private static void EnsureNodeGateIsEnforceable(WorkflowNodeDefinition node, string context)
    {
        if (node.GateMetadata is not { } gate || gate.IsNoGate)
        {
            return;
        }

        var declaration = DescribeGate(gate);

        if (node.Kind == WorkflowNodeKind.TerminalOutcome)
        {
            if (gate.RequiredReviewerRoles.Count == 0
                && !gate.RequiresUserApproval
                && gate.ArtifactRequirement is null)
            {
                return;
            }

            throw Blocked(
                WorkflowTemplateExecutionBlockers.TerminalNodeGate,
                $"The {context} declares stage gates on its terminal outcome node '{node.NodeId}': "
                    + $"{declaration}. A terminal outcome has no transition out of it, so no run would ever "
                    + "evaluate a reviewer, an approval or an artifact requirement declared there, and the "
                    + "template is refused before a run is inserted instead of being completed as if those "
                    + "gates did not exist.");
        }

        if (gate.ArtifactRequirement is null && (gate.RequiredReviewerRoles.Count > 0 || gate.RequiresUserApproval))
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.UnrepresentableNodeGate,
                $"The {context} declares stage gates on node '{node.NodeId}': {declaration}, and no artifact "
                    + "requirement. A reviewer verdict and a user approval are decided against the hash of a "
                    + "stored artifact, so without one there is no content for this stage to be reviewed on, "
                    + "and the template is refused before a run is inserted instead of being executed "
                    + "without those gates.");
        }
    }

    private static string DescribeGate(WorkflowNodeGateMetadata gate) =>
        $"stage kind '{gate.StageKind}', {gate.RequiredReviewerRoles.Count} required reviewer(s) "
            + $"({string.Join(", ", gate.RequiredReviewerRoles)}), user approval "
            + $"{(gate.RequiresUserApproval ? "required" : "not required")} and artifact requirement "
            + $"'{gate.ArtifactRequirement ?? "none"}'";

    /// <summary>
    /// Orders the nodes the run is pinned to, and proves the graph is exactly that chain plus the failure
    /// targets it declares.
    ///
    /// The first pass walks the single success chain from the entry node. Every accepted non-terminal node
    /// has exactly one success target, so a second success exit is a revisit and a branch shows up as a
    /// revisit of the node two branches share; both are refused as a cycle, because a scheme is a list of
    /// stages and a loop along the success path cannot be written as one.
    ///
    /// The second pass adds the nodes the declared failure branches reach, breadth first and in declaration
    /// order, skipping the ones the chain already contains. That is what keeps a failure edge out of a
    /// declared node - the shipped built-in sends two stages back to the code stage - from being reported as
    /// an unreached island, while a node no edge of any kind reaches is still refused by name.
    ///
    /// This runs before the domain graph validation on purpose. Its rules are strictly stronger than the
    /// domain rules for the accepted subset, so evaluating them first reports the more specific reason; the
    /// domain validation still runs afterwards and still names an entry node or a target that does not
    /// exist.
    /// </summary>
    private static IReadOnlyList<WorkflowNodeDefinition> ResolveExecutionOrder(
        WorkflowGraph graph,
        string context)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<WorkflowNodeDefinition>(graph.Nodes.Count);
        var currentNodeId = graph.EntryNodeId;

        try
        {
            while (true)
            {
                if (!visited.Add(currentNodeId))
                {
                    throw Blocked(
                        WorkflowTemplateExecutionBlockers.Cycle,
                        $"The {context} visits node '{currentNodeId}' more than once along its success chain, "
                            + "so it is not a linear chain of stages.");
                }

                var node = graph.GetRequiredNode(currentNodeId);
                order.Add(node);

                if (node.Kind == WorkflowNodeKind.TerminalOutcome)
                {
                    break;
                }

                currentNodeId = node.SuccessTargetNodeId!;
            }

            // The declared failure branches, added after the chain so the stage order still reads as the
            // sequence a run walks. A failure target the chain already contains is a back edge, which is
            // exactly what the shipped built-in declares, and it adds no stage.
            var pending = new Queue<WorkflowNodeDefinition>(order);

            while (pending.Count > 0)
            {
                var declared = pending.Dequeue();
                foreach (var targetNodeId in new[] { declared.SuccessTargetNodeId, declared.FailureTargetNodeId })
                {
                    if (targetNodeId is null || !visited.Add(targetNodeId)) continue;

                    var target = graph.GetRequiredNode(targetNodeId);
                    order.Add(target);
                    pending.Enqueue(target);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.InvalidGraph,
                $"The {context} does not store a valid workflow graph: {exception.Message}");
        }

        var unreached = graph.Nodes
            .Where(node => !visited.Contains(node.NodeId))
            .Select(node => node.NodeId)
            .OrderBy(nodeId => nodeId, StringComparer.Ordinal)
            .ToArray();

        if (unreached.Length > 0)
        {
            throw Blocked(
                WorkflowTemplateExecutionBlockers.UnreachedNode,
                $"The {context} declares node(s) {string.Join(", ", unreached)} that neither the success chain "
                    + $"nor any declared failure target from its entry node '{graph.EntryNodeId}' ever reaches.");
        }

        return order;
    }

    private static WorkflowTemplateExecutionBlockedException Blocked(string blocker, string message) =>
        new(blocker, $"[{blocker}] {message}");
}
