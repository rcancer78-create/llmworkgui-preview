using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// The durable binding between a persisted reviewer execution and the exact run, stage, reviewer role and
/// stored artifact it was about.
/// <para>
/// This is the counterpart of <see cref="ReviewerVerdictRecord.ExecutionId"/>: the verdict names the
/// execution, and this value is what the execution actually recorded. A verdict alone is a claim, and this
/// is the row that either supports it or does not, so the transition gate reads both and requires them to
/// agree.
/// </para>
/// <para>
/// The route fields are deliberately separate. <see cref="RequestedRouteId"/> is what the product asked
/// for and is written before dispatch; <see cref="ObservedRouteId"/> is only ever what a backend reported
/// back, and it stays null while no such report exists. No code path may copy the first into the second: a
/// turn that was never confirmed to have reached the assigned model is not evidence that it did.
/// </para>
/// </summary>
public sealed class ReviewerExecutionEvidence
{
    public ReviewerExecutionEvidence(
        string executionId,
        string sessionId,
        string workflowRunId,
        string reviewerRole,
        string stageId,
        string requestedRouteId,
        string? observedRouteId,
        string reviewedArtifactId,
        string reviewedArtifactHash,
        bool isReadOnly,
        ExecutionState executionState)
    {
        ExecutionId = DomainGuard.NotBlank(executionId, nameof(executionId));
        SessionId = DomainGuard.NotBlank(sessionId, nameof(sessionId));
        WorkflowRunId = DomainGuard.NotBlank(workflowRunId, nameof(workflowRunId));
        ReviewerRole = DomainGuard.NotBlank(reviewerRole, nameof(reviewerRole));
        StageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        RequestedRouteId = DomainGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = DomainGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        ReviewedArtifactId = DomainGuard.NotBlank(reviewedArtifactId, nameof(reviewedArtifactId));
        ReviewedArtifactHash = DomainGuard.NotBlank(reviewedArtifactHash, nameof(reviewedArtifactHash));
        IsReadOnly = isReadOnly;
        ExecutionState = executionState;
    }

    public string ExecutionId { get; }

    public string SessionId { get; }

    public string WorkflowRunId { get; }

    public string ReviewerRole { get; }

    public string StageId { get; }

    /// <summary>The route the product asked for, written before the turn was dispatched.</summary>
    public string RequestedRouteId { get; }

    /// <summary>
    /// The route a backend actually reported, or null when no backend has reported one. A dispatch that
    /// never reached a model, a cancelled turn, an ambiguous delivery and a crash all leave this null, and
    /// null is never treated as agreement with <see cref="RequestedRouteId"/>.
    /// <para>
    /// The store reads this out of the execution row rather than out of the binding, and refuses to write a
    /// value the execution did not already record, so a value that reaches this property is always something
    /// a backend's answer produced rather than something the calling process asserted.
    /// </para>
    /// </summary>
    public string? ObservedRouteId { get; }

    public string ReviewedArtifactId { get; }

    public string ReviewedArtifactHash { get; }

    /// <summary>Whether the reviewer turn was persisted as read-only, so it could not have changed anything.</summary>
    public bool IsReadOnly { get; }

    public ExecutionState ExecutionState { get; }

    /// <summary>Returns a copy of this evidence carrying what a backend reported, or nothing new.</summary>
    public ReviewerExecutionEvidence WithObservedOutcome(string? observedRouteId, ExecutionState executionState) =>
        new(
            ExecutionId,
            SessionId,
            WorkflowRunId,
            ReviewerRole,
            StageId,
            RequestedRouteId,
            observedRouteId,
            ReviewedArtifactId,
            ReviewedArtifactHash,
            IsReadOnly,
            executionState);

    /// <summary>
    /// Returns a copy of this evidence naming a different run. A recorded turn cannot actually be re-pointed
    /// at another run - the store's immutability trigger and the narrow UPDATE list both refuse that - so
    /// this exists so the refusal can be shown rather than merely described: a caller that tries gets its own
    /// copy back, and the stored row is the one that stays.
    /// </summary>
    public ReviewerExecutionEvidence WithIdentity(string workflowRunId) =>
        new(
            ExecutionId,
            SessionId,
            workflowRunId,
            ReviewerRole,
            StageId,
            RequestedRouteId,
            ObservedRouteId,
            ReviewedArtifactId,
            ReviewedArtifactHash,
            IsReadOnly,
            ExecutionState);

    /// <summary>
    /// Why this execution cannot authorize a verdict for <paramref name="artifact"/>, or null when it can.
    /// <para>
    /// Every clause is a distinct way the evidence can fail to describe what the gate needs it to describe,
    /// and each one is named rather than collapsed into "no evidence": a verdict from a different run, stage
    /// or role; a turn that did not succeed; a turn that was not read-only; a route that was requested but
    /// never observed, or observed as something else; and an artifact that is no longer the one the gate is
    /// about.
    /// </para>
    /// </summary>
    public string? FindUnsatisfiedReason(
        string workflowRunId,
        string stageId,
        string reviewerRole,
        WorkflowArtifactEvidence artifact,
        ReviewerVerdictRecord verdict)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(verdict);

        if (!string.Equals(WorkflowRunId, workflowRunId, StringComparison.Ordinal))
        {
            return $"the reviewer execution belongs to run '{WorkflowRunId}', not to run '{workflowRunId}'";
        }

        if (!string.Equals(StageId, stageId, StringComparison.Ordinal))
        {
            return $"the reviewer execution was recorded on stage '{StageId}', not on stage '{stageId}'";
        }

        if (!string.Equals(ReviewerRole, reviewerRole, StringComparison.Ordinal))
        {
            return $"the reviewer execution was recorded for role '{ReviewerRole}', not for role '{reviewerRole}'";
        }

        if (ExecutionState != ExecutionState.Succeeded)
        {
            return $"the reviewer execution ended in state '{ExecutionState}' instead of 'Succeeded'";
        }

        if (!IsReadOnly)
        {
            return "the reviewer execution was not recorded as read-only";
        }

        if (ObservedRouteId is null)
        {
            return $"no backend reported an observed route for the reviewer execution; the execution row itself "
                + $"records none, and only the requested route '{RequestedRouteId}' was written";
        }

        if (!string.Equals(ObservedRouteId, RequestedRouteId, StringComparison.Ordinal))
        {
            return $"the backend observed route '{ObservedRouteId}' while route '{RequestedRouteId}' was requested";
        }

        if (!string.Equals(ReviewedArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
        {
            return $"the reviewer execution reviewed artifact '{ReviewedArtifactId}', not the current artifact "
                + $"'{artifact.ArtifactId}' of the stage";
        }

        if (!string.Equals(ReviewedArtifactHash, artifact.HashSha256, StringComparison.Ordinal))
        {
            return $"the reviewer execution reviewed hash '{ReviewedArtifactHash}', not the current verified hash "
                + $"'{artifact.HashSha256}' of the stage";
        }

        if (!string.Equals(verdict.RouteId, RequestedRouteId, StringComparison.Ordinal))
        {
            return $"the verdict names route '{verdict.RouteId}' while the reviewer execution requested "
                    + $"'{RequestedRouteId}'";
        }

        if (!string.Equals(verdict.StageId, StageId, StringComparison.Ordinal))
        {
            return $"the verdict names stage '{verdict.StageId ?? "<none>"}' while the reviewer execution "
                + $"was recorded on stage '{StageId}'";
        }

        if (!string.Equals(verdict.ReviewedArtifactId, ReviewedArtifactId, StringComparison.Ordinal))
        {
            return $"the verdict names artifact '{verdict.ReviewedArtifactId ?? "<none>"}' while the reviewer "
                + $"execution reviewed artifact '{ReviewedArtifactId}'";
        }

        return null;
    }
}
