namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The named reasons a workflow run cannot be advanced along a declared failure route.
///
/// A template may declare a failure target on a stage, and that edge is preserved on the run: it is in the
/// pinned graph, it is the derived stage's own <c>FailureStageId</c>, and it survives a restart because the
/// pinned scheme snapshot carries it. What is deliberately absent is any transition that follows it.
/// <see cref="IWorkflowRunService.AdvanceStageAsync"/> only ever follows the success target, so a rejected
/// review leaves the run where it is instead of walking it to the stage the template sends failures back to.
///
/// Taking that edge would need evidence that the current run has no way to produce and this service no way
/// to check: the aggregate's transition gate authorizes a move to the stage a stage names as <em>next</em>,
/// and a non-unanimous verdict is the one thing it refuses rather than reroutes. Deciding that any
/// non-approving verdict reroutes to a failure target - for any of a stage's required reviewers, over a
/// graph whose failure edges point backwards - would be a new safety rule invented here, and a
/// backward-pointing edge with no declared budget would loop without end. So the edge is kept as evidence of
/// what the template declared, and the attempt is refused by name instead.
/// </summary>
public static class WorkflowRunFailureRouteRefusals
{
    /// <summary>
    /// The run's current stage declares no failure target at all, so there is no failure route to refuse -
    /// and, equally, no failure route that could have been taken by accident.
    /// </summary>
    public const string NoDeclaredFailureRoute = "workflow-stage-declares-no-failure-route";

    /// <summary>
    /// The run's current stage does declare a failure target, and this slice cannot take it. The refusal
    /// names the stage and the declared target so the operator can see the edge the template holds, without
    /// the run being moved along it and without the failure route being reported as the success route.
    /// </summary>
    public const string FailureRouteNotExecutable = "workflow-stage-failure-route-not-executable";
}
