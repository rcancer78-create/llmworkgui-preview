using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// Commands of the workflow run aggregate. Every command works against the persisted run identified by
/// its id.
///
/// A run carries two independent identities. <see cref="StartRunAsync"/> records both at once: the source
/// workflow version the caller selected, and the template version currently assigned to the project,
/// together with the graph and the execution scheme derived from it. Those three template values are
/// written once and are never rewritten, so editing the template or moving the assignment afterwards
/// cannot reach an existing run. A project with no assignment, with an assignment to a version that no
/// longer exists, or with a template this slice cannot execute faithfully is refused by a named blocker
/// and no run is created.
/// </summary>
public interface IWorkflowRunService
{
    /// <summary>
    /// Starts a run pinned to the template version currently assigned to the project, or refuses with a
    /// <see cref="WorkflowTemplateExecutionBlockedException"/>. There is no silent fallback to the
    /// process-wide standard scheme.
    /// </summary>
    Task<WorkflowRun> StartRunAsync(
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a run that carries no template identity at all and follows the process-wide standard scheme.
    ///
    /// This is the documented legacy path and the only way to reproduce a pre-existing run: such a run
    /// records no template and, on every later read, keeps advancing against the standard scheme exactly
    /// as it did before template pinning existed. Product code that starts a run for a real project is
    /// expected to use <see cref="StartRunAsync"/> instead.
    /// </summary>
    Task<WorkflowRun> StartLegacyRunAsync(
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the dispatch linkage, then refuses reviewer verdict admission because this build has no
    /// persisted, hash-bound model response and parser-produced verdict contract.
    /// <para>
    /// The evidence that arrived with the call is never believed on its own. The verdict must name the
    /// execution it was read out of; that execution must exist; it must be a succeeded, read-only turn of
    /// this run, on the stage the run currently sits on, for the role the verdict names, whose observed
    /// route equals the route the verdict names and whose reviewed artifact is the run's newest stored,
    /// re-hashed artifact of the kind that stage requires. Those metadata checks do not establish the
    /// response content or verdict. Caller-supplied verdicts, comments and timestamps are never accepted
    /// as a substitute for missing parsed response evidence.
    /// </para>
    /// <para>
    /// A verdict with no execution, a cancelled or ambiguous execution, a route that was requested but
    /// never observed and a verdict of a replaced artifact are all refused by name. Fully matching
    /// dispatch metadata is also refused until the response evidence contract exists.
    /// </para>
    /// </summary>
    Task<WorkflowRun> RecordReviewerVerdictAsync(
        string runId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an unlinked verdict on the explicitly legacy, non-model path, and refuses a run pinned to a
    /// template version.
    /// <para>
    /// This exists so a run that predates template pinning, and the history already recorded on such runs,
    /// stay readable. It is the only way a role, a route string and a hash become a stored verdict with no
    /// reviewer execution behind them, and it is deliberately unusable for a production model-review stage:
    /// a pinned run refuses it here and the aggregate refuses it again, so the rule does not depend on this
    /// being the only door. Verdicts already persisted on a pinned run before this rule existed are not
    /// deleted - they simply stop satisfying the gate.
    /// </para>
    /// </summary>
    Task<WorkflowRun> RecordLegacyUnlinkedReviewerVerdictAsync(
        string runId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an explicit user decision about the run's current pinned stage artifact, and nothing else.
    /// <para>
    /// The evidence that arrived with the call is never believed on its own. The stage is resolved against
    /// the run's own scheme and has to be the stage the run actually sits on; the artifact kind comes from
    /// that stage rather than from the caller; the artifact is the newest stored one of that run, that stage
    /// and that kind; and the committed bytes behind it are re-verified before anything is written. Only
    /// then is the caller's stage and hash compared exactly against that artifact, so a stale hash, a
    /// replaced document and a missing or altered blob are all refused here rather than only in a screen.
    /// </para>
    /// <para>
    /// The recorded approver is resolved by the service itself from
    /// <see cref="IUserApprovalIdentity"/> and replaces whatever <see cref="UserApprovalEvidence.ApprovedBy"/>
    /// carried, so no caller can record a decision under somebody else's name, and an identity that cannot
    /// be established is a refusal instead of a fallback name. A rejection is a terminal transition, exactly
    /// as the aggregate records it.
    /// </para>
    /// </summary>
    Task<WorkflowRun> RecordUserApprovalAsync(
        string runId,
        UserApprovalEvidence approval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the run to the next stage of the scheme it is pinned to - or to the standard scheme for a
    /// legacy run - only when that stage's required artifact is a stored, run-scoped artifact of this run
    /// and this stage whose bytes still hash to what was recorded, and only when the required reviewers and
    /// the user approval agree on that same hash. The recorded artifact id and hash become part of the
    /// transition evidence.
    /// <para>
    /// On a run pinned to a template version there is one further requirement: every authorizing reviewer
    /// verdict must be linked to a persisted reviewer execution, read from storage at the moment of the
    /// transition, that is a succeeded read-only turn of this run, this stage and this role whose observed
    /// route equals the route the verdict names and whose reviewed artifact is the very artifact the gate
    /// is about. An unlinked verdict - including one persisted before this rule existed - authorizes
    /// nothing, and neither does a cancelled, ambiguous, mismatched or replaced execution.
    /// </para>
    /// </summary>
    Task<WorkflowRun> AdvanceStageAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The declared failure route of the run's current stage, or the named reason it cannot be taken.
    /// <para>
    /// This is the only door to a failure transition, and it is deliberately closed. The run aggregate
    /// distinguishes a success transition from a terminal outcome and nothing else: it authorizes a move to
    /// the stage a stage names as its next one, and a non-unanimous reviewer verdict is refused rather than
    /// rerouted. A failure edge is therefore preserved - on the pinned graph, on the derived stage and
    /// across a restart - and never followed, so a rejected review leaves the run on the stage whose gate
    /// rejected it instead of walking it to the failure target the template declares, and never to the
    /// success target either.
    /// </para>
    /// <para>
    /// The refusal is an exception, not a no-op, and it always names one of
    /// <see cref="WorkflowRunFailureRouteRefusals"/> together with the run, its current stage and the
    /// declared target. The run is never modified: no stage moves, no transition is recorded, no terminal
    /// outcome is set and nothing is written. A node's execution status is never a workflow terminal
    /// outcome, and this method never conflates the two.
    /// </para>
    /// </summary>
    Task<WorkflowRun> AdvanceToDeclaredFailureStageAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the content a stage produced as the durable artifact that can authorize its next transition.
    /// <para>
    /// The stage must be the run's current stage and the kind must be exactly the artifact requirement that
    /// stage declares in the run's own scheme, so a stale stage, a wrong kind and a terminal run are all
    /// refused. The bytes are stored first and the artifact row and the run's evidence state are committed
    /// in one transaction: a failure leaves neither a persisted row nor an in-memory run that could authorize
    /// on evidence that was never committed. The content itself is never logged.
    /// </para>
    /// </summary>
    Task<WorkflowRun> RecordStageArtifactAsync(
        string runId,
        string stageId,
        string kind,
        Stream content,
        DataClassification classification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores bytes with a verified run/project execution association. This does not turn an operator's
    /// file into an observed native response or a reviewer verdict. Unsupported stores refuse the call.
    /// </summary>
    Task<WorkflowArtifactRecordingResult> RecordExecutionArtifactAsync(string runId, string stageId, string kind,
        string executionId, Stream content, DataClassification classification,
        CancellationToken cancellationToken = default) =>
        throw new WorkflowValidationException("Execution-associated artifact collection is unavailable.");

    Task<WorkflowRun> CancelRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun> FailRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a pinned run only after it reaches the terminal stage and that stage's gate is verified.
    /// Explicit legacy runs retain administrative completion without a pinned-stage claim.
    /// Cancellation and failure remain available before the terminal stage.
    /// </summary>
    Task<WorkflowRun> CompleteRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default);
}
