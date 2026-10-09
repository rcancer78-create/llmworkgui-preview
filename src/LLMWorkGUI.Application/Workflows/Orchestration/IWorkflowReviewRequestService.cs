namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// Requests the assigned model review of a run's current stage artifact, for every reviewer role that
/// stage declares, and persists what can be truthfully persisted about each of them.
/// <para>
/// The contract this interface exists to keep is that a request is resolved entirely from the run's own
/// pinned identity. The roles come from the stage the run's pinned scheme declares, the route for each role
/// comes from the pinned template's own role binding, and the content comes from the run's newest stored
/// artifact of the kind that stage requires - re-hashed and re-read through the blob store immediately
/// before it is used. Nothing is taken from typed text, from a screen, from a mutable template or from a
/// route label.
/// </para>
/// <para>
/// Admission and outcomes are per role. A preflight refusal writes no rows for that role, while other
/// roles in the same request may have dispatched. Production commits session, execution and binding
/// together before dispatch, and commits the observed terminal outcome separately afterward. A crash
/// after admission can leave queued evidence that authorizes nothing; it is not automatically retried.
/// </para>
/// <para>
/// This slice deliberately records no verdict. A dispatched request reports the execution state and the
/// observed route exactly as they were persisted; parsing a model's answer into an Approve or a Reject
/// requires persisted response evidence and a parser contract that this build does not provide.
/// </para>
/// </summary>
public interface IWorkflowReviewRequestService
{
    Task<WorkflowReviewRequestResult> RequestAssignedReviewAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
