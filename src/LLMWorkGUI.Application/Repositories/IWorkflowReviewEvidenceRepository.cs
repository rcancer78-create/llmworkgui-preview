using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// The durable bindings between reviewer executions and the runs, stages, roles and artifacts they were
/// about. This is the only place a <see cref="ReviewerExecutionEvidence"/> is stored or read, and it is
/// what makes a reviewer verdict checkable rather than merely asserted.
/// <para>
/// Reads are the authority for the transition gate: a verdict is only honored while a binding for its
/// execution still exists and still agrees with the run, the stage, the role, the route and the current
/// verified artifact. A verdict whose execution was never bound, was cancelled, stayed ambiguous, or was
/// bound to something else is not satisfied by this repository, and no cache, projection or in-memory
/// shortcut may stand in for it.
/// </para>
/// </summary>
public interface IWorkflowReviewEvidenceRepository
{
    /// <summary>
    /// Every binding recorded for <paramref name="workflowRunId"/>, oldest first. A run service resolves
    /// this immediately before it asks the aggregate to advance, so a binding that was never written, or
    /// one that has since been contradicted, is simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<ReviewerExecutionEvidence>> ListByRunIdAsync(
        string workflowRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The binding of one execution, or null when no binding carries that execution id. This is the read a
    /// verdict is verified against, and a null answer means the verdict is unlinked.
    /// </summary>
    Task<ReviewerExecutionEvidence?> GetByExecutionIdAsync(
        string executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the binding of a reviewer turn before the turn is dispatched. The requested route, the run,
    /// the stage, the role, the reviewed artifact and the read-only flag are all persisted here, and the
    /// observed route is left null because at this moment no backend has reported one.
    /// <para>
    /// The store refuses a second binding for the same execution and a second binding of the same run,
    /// stage, role and artifact hash, so a replayed request is rejected by storage rather than by whichever
    /// caller happened to be first.
    /// </para>
    /// </summary>
    Task SaveAsync(ReviewerExecutionEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a backend reported for an already-bound turn: the observed route, exactly as it was
    /// reported, and nothing else. The binding's run, stage, role, artifact, requested route and read-only
    /// flag are immutable, so this call cannot turn a requested route into an observed one.
    /// <para>
    /// The execution row is the authority for the observed route, and this call requires the value it is
    /// given to be exactly the one that execution already carries. An observation no execution recorded -
    /// including the null an unobserved, cancelled, ambiguous or lost turn has - is refused here and is not
    /// written, and a read of this binding reports the execution's own route rather than whatever a caller
    /// last passed to this method. A store therefore cannot be talked into promoting a requested route into
    /// an observed one, which is the whole point of keeping the two columns apart.
    /// </para>
    /// </summary>
    Task UpdateObservedAsync(ReviewerExecutionEvidence evidence, CancellationToken cancellationToken = default);
}
