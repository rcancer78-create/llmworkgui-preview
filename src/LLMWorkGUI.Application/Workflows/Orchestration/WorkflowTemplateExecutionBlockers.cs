namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The named reasons a workflow template version cannot be pinned to a run yet.
///
/// A run is never started against a template that this slice cannot execute faithfully, and the reason is
/// always one of these names, so a refusal is a specific, reportable blocker rather than a generic
/// failure. Adding a node kind, a gate or a second exit path to a template is therefore a deliberate
/// change: it has to add support here first, and it cannot quietly slip into a run as a dropped edge.
/// </summary>
public static class WorkflowTemplateExecutionBlockers
{
    /// <summary>The project has no assigned template version, so there is nothing to pin.</summary>
    public const string MissingAssignment = "no-template-assignment";

    /// <summary>The assignment names a version that the template store does not hold.</summary>
    public const string MissingTemplateVersion = "assigned-template-version-not-found";

    /// <summary>The stored graph does not validate as a workflow graph at all.</summary>
    public const string InvalidGraph = "invalid-workflow-graph";

    /// <summary>
    /// The stored graph of the shipped built-in standard template is not the graph
    /// <c>WorkflowStudioService.CreateStandardTemplate()</c> produces any more, so the run would be pinned
    /// to a plan that is no longer the shipped one. The built-in is pinned exactly or not at all: it is
    /// never rebuilt from the process-wide standard scheme and never approximated, so a changed or
    /// corrupted built-in graph refuses here - before a run row exists - with the first difference named.
    /// </summary>
    public const string BuiltInStandardTemplateGraphChanged = "built-in-standard-template-graph-changed";

    /// <summary>
    /// A node whose kind cannot be represented as a scheme stage: a kind this slice does not map at all
    /// (a condition, a retry, an escalation, a user decision or an artifact collection), or one of the
    /// kinds it does map whose declared gate does not say what the kind names. A kind is a label, so it
    /// never supplies a gate; it may only stand in for a gate the node actually declared.
    /// </summary>
    public const string UnsupportedNodeKind = "unsupported-node-kind";

    /// <summary>
    /// A non-terminal node whose failure target names a node the graph does not declare. A failure edge
    /// onto a declared node is preserved on the run and is not followed by a transition; one onto an
    /// undeclared node is not a graph this slice can represent at all.
    /// </summary>
    public const string FailureTransition = "unsupported-failure-transition";

    /// <summary>A non-terminal node without exactly one success target.</summary>
    public const string SuccessTransition = "unsupported-success-transition";

    /// <summary>A node reached more than once on the walk from the entry node.</summary>
    public const string Cycle = "unsupported-cycle";

    /// <summary>A node the walk from the entry node never reaches, which is how a second exit shows up.</summary>
    public const string UnreachedNode = "unreached-node";

    /// <summary>A terminal outcome node that declares a transition.</summary>
    public const string TerminalTransition = "terminal-outcome-declares-a-transition";

    /// <summary>
    /// A node that declares a condition expression.</summary>
    public const string Condition = "unsupported-condition";

    /// <summary>A node that declares a fallback route.</summary>
    public const string FallbackRoute = "unsupported-fallback-route";

    /// <summary>A node that declares an artifact contract.</summary>
    public const string ArtifactContract = "unsupported-artifact-contract";

    /// <summary>A node that declares a permission intent.</summary>
    public const string PermissionIntent = "unsupported-permission-intent";

    /// <summary>
    /// A terminal outcome node that declares a retry budget. A retry budget belongs to node execution and
    /// is preserved on the pinned graph of a non-terminal node, where it describes the execution that would
    /// happen before the transition. A terminal outcome node performs no execution, so a budget declared
    /// there describes nothing any run could ever read, and it is refused rather than preserved as a field
    /// with no referent.
    /// </summary>
    public const string RetryBudget = "unsupported-retry-budget";

    /// <summary>
    /// A node that declares stage gates this slice cannot turn into a gate a run actually evaluates -
    /// a required reviewer or a user approval with no artifact requirement, or any other tuple the
    /// derived scheme refuses. The refusal exists so that a preserved gate is never silently replaced by
    /// a weaker one; a node whose metadata is absent or the explicit no-gate tuple keeps the gate-free
    /// mapping, because a document that never declared a gate is not evidence of one that was dropped.
    /// </summary>
    public const string UnrepresentableNodeGate = "unrepresentable-node-gate";

    /// <summary>
    /// A terminal outcome node that declares a required reviewer, a user approval or an artifact
    /// requirement. Terminal completion does not evaluate transition gates, so a run could never
    /// enforce such a declaration and would finish as if it did not exist. The refusal happens before a
    /// run is inserted, not at the terminal transition that would quietly ignore it.
    /// </summary>
    public const string TerminalNodeGate = "unsupported-terminal-node-gate";
}
