using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.Entities;

/// <summary>
/// The workflow run aggregate. It is pinned to the workflow version it was started with, keeps its own
/// stages, roles, verdicts and user approvals, and reaches a terminal workflow outcome only through an
/// explicit terminal transition — never automatically from a single execution result (ТЗ §6.15).
/// </summary>
public sealed class WorkflowRun
{
    private readonly List<WorkflowTransitionRecord> _transitions;
    private readonly List<ReviewerVerdictRecord> _verdicts;
    private readonly List<UserApprovalEvidence> _approvals;
    private readonly List<WorkflowArtifactEvidence> _artifacts;

    public WorkflowRun(
        string id,
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId,
        WorkflowRunState state,
        string currentStageId,
        string currentRole,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? endedAtUtc,
        WorkflowTerminalOutcome terminalOutcome,
        string? terminalReason,
        IReadOnlyList<WorkflowTransitionRecord> transitions,
        IReadOnlyList<ReviewerVerdictRecord> verdicts,
        IReadOnlyList<UserApprovalEvidence> approvals,
        string? templateId = null,
        int? templateVersion = null,
        string? templateGraphSnapshotJson = null,
        string? templateSchemeSnapshotJson = null,
        IReadOnlyList<WorkflowArtifactEvidence>? artifacts = null)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        ProjectId = DomainGuard.NotBlank(projectId, nameof(projectId));
        WorkflowPackageId = DomainGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        WorkflowVersionId = DomainGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));
        SessionId = DomainGuard.OptionalNotBlank(sessionId, nameof(sessionId));
        State = state;
        CurrentStageId = DomainGuard.NotBlank(currentStageId, nameof(currentStageId));
        CurrentRole = DomainGuard.NotBlank(currentRole, nameof(currentRole));
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        TerminalOutcome = terminalOutcome;
        TerminalReason = DomainGuard.OptionalNotBlank(terminalReason, nameof(terminalReason));
        _transitions = DomainGuard.NotNullList(transitions, nameof(transitions)).ToList();
        _verdicts = DomainGuard.NotNullList(verdicts, nameof(verdicts)).ToList();
        _approvals = DomainGuard.NotNullList(approvals, nameof(approvals)).ToList();
        _artifacts = artifacts is null
            ? new List<WorkflowArtifactEvidence>()
            : DomainGuard.NotNullList(artifacts, nameof(artifacts)).ToList();
        TemplateId = DomainGuard.OptionalNotBlank(templateId, nameof(templateId));
        TemplateVersion = templateVersion;
        TemplateGraphSnapshotJson = DomainGuard.OptionalNotBlank(
            templateGraphSnapshotJson,
            nameof(templateGraphSnapshotJson));
        TemplateSchemeSnapshotJson = DomainGuard.OptionalNotBlank(
            templateSchemeSnapshotJson,
            nameof(templateSchemeSnapshotJson));

        EnsureTemplateIdentityIsWhole();
        EnsureArtifactsBelongToThisRun();
        EnsureChronology();
        EnsureTerminalConsistency();
    }

    public string Id { get; }

    public string ProjectId { get; }

    public string WorkflowPackageId { get; }

    /// <summary>
    /// The workflow version this run was started with. The value is fixed for the whole lifetime of the
    /// run, even when the project's active-version pointer is moved later.
    /// </summary>
    public string WorkflowVersionId { get; }

    public string? SessionId { get; private set; }

    public WorkflowRunState State { get; private set; }

    public string CurrentStageId { get; private set; }

    public string CurrentRole { get; private set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public WorkflowTerminalOutcome TerminalOutcome { get; private set; }

    public string? TerminalReason { get; private set; }

    public IReadOnlyList<WorkflowTransitionRecord> Transitions => _transitions.AsReadOnly();

    public IReadOnlyList<ReviewerVerdictRecord> Verdicts => _verdicts.AsReadOnly();

    public IReadOnlyList<UserApprovalEvidence> Approvals => _approvals.AsReadOnly();

    /// <summary>
    /// The durable artifacts recorded for this run, one entry per stored artifact row that belongs to it.
    /// The list is evidence, not history: it is rebuilt from the persisted rows on every read and is never
    /// derived from the redacted evidence payload of the run.
    /// </summary>
    public IReadOnlyList<WorkflowArtifactEvidence> Artifacts => _artifacts.AsReadOnly();

    /// <summary>
    /// The assigned execution template this run was pinned to, or null for a legacy run that was started
    /// without a template assignment. This is a second, independent identity: it never replaces
    /// <see cref="WorkflowVersionId"/>, which continues to name the source workflow version, and the
    /// source version is never presented as if it contained the template graph.
    /// </summary>
    public string? TemplateId { get; }

    /// <summary>The exact template version pinned to this run, or null for a legacy run.</summary>
    public int? TemplateVersion { get; }

    /// <summary>
    /// The serialized, validated graph of <see cref="TemplateId"/> at <see cref="TemplateVersion"/>, copied
    /// into the run so that advancing the run never depends on the template still holding that content.
    /// </summary>
    public string? TemplateGraphSnapshotJson { get; }

    /// <summary>
    /// The execution scheme derived from <see cref="TemplateGraphSnapshotJson"/>, also copied into the run.
    /// Every stage, reviewer and approval check of a pinned run resolves against this snapshot, never
    /// against a process-wide scheme.
    /// </summary>
    public string? TemplateSchemeSnapshotJson { get; }

    /// <summary>True when the run carries an assigned template identity and both of its snapshots.</summary>
    public bool IsTemplateBacked => TemplateId is not null;

    public bool IsTerminal => IsTerminalState(State);

    public static bool IsTerminalState(WorkflowRunState state) =>
        state is WorkflowRunState.Completed or WorkflowRunState.Failed or WorkflowRunState.Cancelled;

    public static WorkflowRun Start(
        string id,
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId,
        WorkflowStageDefinition initialStage,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(initialStage);

        return new WorkflowRun(
            id,
            projectId,
            workflowPackageId,
            workflowVersionId,
            sessionId,
            WorkflowRunState.Running,
            initialStage.StageId,
            initialStage.RequiredRole,
            startedAtUtc,
            endedAtUtc: null,
            WorkflowTerminalOutcome.None,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>());
    }

    /// <summary>
    /// Starts a run that is pinned to an already resolved template assignment. The two identities are
    /// recorded together: the source workflow version the caller selected and the assigned template
    /// version whose graph and derived scheme the run will execute. No later edit of a template and no
    /// later move of the project assignment can reach a run created here, because none of the four
    /// template values is ever written again.
    /// </summary>
    public static WorkflowRun StartPinnedToTemplate(
        string id,
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId,
        WorkflowStageDefinition initialStage,
        DateTimeOffset startedAtUtc,
        string templateId,
        int templateVersion,
        string templateGraphSnapshotJson,
        string templateSchemeSnapshotJson)
    {
        ArgumentNullException.ThrowIfNull(initialStage);

        return new WorkflowRun(
            id,
            projectId,
            workflowPackageId,
            workflowVersionId,
            sessionId,
            WorkflowRunState.Running,
            initialStage.StageId,
            initialStage.RequiredRole,
            startedAtUtc,
            endedAtUtc: null,
            WorkflowTerminalOutcome.None,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>(),
            templateId,
            templateVersion,
            templateGraphSnapshotJson,
            templateSchemeSnapshotJson);
    }

    /// <summary>
    /// Records a reviewer verdict that names the persisted reviewer execution it was read out of.
    /// <para>
    /// Nothing is checked here beyond the aggregate's own invariants: a linked record is still only a
    /// claim about an execution until the transition gate can match it against the stored
    /// <see cref="ReviewerExecutionEvidence"/>, which is the point at which a run, a stage, a role, an
    /// observed route and an artifact hash all have to agree.
    /// </para>
    /// </summary>
    public void RecordReviewerVerdict(ReviewerVerdictRecord verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        EnsureNotTerminal("record a reviewer verdict");

        if (!verdict.IsLinked)
        {
            throw new ArgumentException(
                "A reviewer verdict that authorizes a model-review stage must name the reviewer execution it "
                    + "was read out of, the stage and the stored artifact row. Use "
                    + "RecordLegacyUnlinkedReviewerVerdict for the explicitly legacy, non-model path.",
                nameof(verdict));
        }

        _verdicts.Add(verdict);
    }

    /// <summary>
    /// Records an unlinked verdict on the explicitly legacy path, for a run that predates template pinning.
    /// <para>
    /// This exists so that a historical run and its recorded verdicts stay readable, and it is deliberately
    /// narrow in two directions. A record that names an execution cannot come through here - it is not an
    /// unlinked verdict - and a run pinned to a template version cannot be given one at all, because such a
    /// verdict is exactly the unlinked approval the model-review gate exists to reject. Verdicts already
    /// persisted on a pinned run before this rule existed are still refused by the gate rather than deleted,
    /// so an old approval becomes unusable instead of silently remaining valid.
    /// </para>
    /// </summary>
    public void RecordLegacyUnlinkedReviewerVerdict(ReviewerVerdictRecord verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        EnsureNotTerminal("record a legacy unlinked reviewer verdict");

        if (IsTemplateBacked)
        {
            throw new InvalidStateTransitionException(
                nameof(WorkflowRun),
                State,
                TerminalOutcome,
                $"the run is pinned to template '{TemplateId}' version {TemplateVersion} and refuses an "
                    + "unlinked reviewer verdict, because only a verdict backed by a persisted reviewer "
                    + "execution can authorize a pinned model-review stage");
        }

        if (verdict.IsLinked)
        {
            throw new ArgumentException(
                "A linked reviewer verdict is not an unlinked one; use RecordReviewerVerdict.",
                nameof(verdict));
        }

        _verdicts.Add(verdict);
    }

    /// <summary>
    /// Records an explicit user decision about a stage artifact of this run.
    /// <para>
    /// The aggregate refuses a decision that contradicts its own stored evidence: one for a stage the run
    /// has already left, and one whose hash is not the hash of the run's newest stored artifact of that
    /// stage. That is the same rule the transition gate later applies, stated at the moment of recording, so
    /// an approval of a document that has since been replaced cannot even be written - by this screen, by a
    /// recovery scenario or by any other caller of the aggregate. A run that has recorded no artifact for
    /// that stage yet has nothing to contradict; whether an approval needs stored content at all is decided
    /// by the run service, which resolves the stage's required kind and verifies the bytes behind it.
    /// </para>
    /// </summary>
    public void RecordUserApproval(UserApprovalEvidence approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        EnsureNotTerminal("record a user approval");

        if (approval.DecidedAtUtc < StartedAtUtc)
        {
            throw new ArgumentException(
                "A user approval cannot predate the workflow run.",
                nameof(approval));
        }

        EnsureApprovalMatchesTheCurrentArtifact(approval);

        _approvals.Add(approval);

        if (approval.Decision == UserApprovalDecision.Rejected)
        {
            State = WorkflowRunState.Failed;
            TerminalOutcome = WorkflowTerminalOutcome.Rejected;
            EndedAtUtc = approval.DecidedAtUtc;
            TerminalReason = approval.Comment;
        }
    }

    private void EnsureApprovalMatchesTheCurrentArtifact(UserApprovalEvidence approval)
    {
        if (!string.Equals(CurrentStageId, approval.StageId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The run is at stage '{CurrentStageId}', so an approval for stage '{approval.StageId}' is refused.",
                nameof(approval));
        }

        var current = _artifacts
            .Where(artifact => string.Equals(artifact.RunId, Id, StringComparison.Ordinal)
                && string.Equals(artifact.StageId, approval.StageId, StringComparison.Ordinal))
            .OrderByDescending(artifact => artifact.CreatedAtUtc)
            .ThenByDescending(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (current is null)
        {
            return;
        }

        if (!string.Equals(current.HashSha256, approval.ArtifactHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The current stored artifact '{current.ArtifactId}' of stage '{approval.StageId}' has hash "
                    + $"'{current.HashSha256}', so an approval of '{approval.ArtifactHash}' is refused.",
                nameof(approval));
        }
    }

    /// <summary>An independent snapshot of this aggregate before an optimistic persisted mutation.</summary>
    public WorkflowRun Snapshot() => new(Id, ProjectId, WorkflowPackageId, WorkflowVersionId, SessionId,
        State, CurrentStageId, CurrentRole, StartedAtUtc, EndedAtUtc, TerminalOutcome, TerminalReason,
        _transitions, _verdicts, _approvals, TemplateId, TemplateVersion, TemplateGraphSnapshotJson,
        TemplateSchemeSnapshotJson, _artifacts);

    /// <summary>
    /// Returns a copy carrying one more artifact. The original is untouched, so a failed artifact insert
    /// cannot leave the original in-memory run authorizing a transition on uncommitted evidence.
    /// </summary>
    public WorkflowRun WithArtifact(WorkflowArtifactEvidence artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        EnsureNotTerminal("record a stage artifact");

        if (!string.Equals(artifact.RunId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The artifact belongs to run '{artifact.RunId}', not to run '{Id}'.",
                nameof(artifact));
        }

        var artifacts = new WorkflowArtifactEvidence[_artifacts.Count + 1];
        _artifacts.CopyTo(artifacts, 0);
        artifacts[^1] = artifact;

        return new WorkflowRun(
            Id,
            ProjectId,
            WorkflowPackageId,
            WorkflowVersionId,
            SessionId,
            State,
            CurrentStageId,
            CurrentRole,
            StartedAtUtc,
            EndedAtUtc,
            TerminalOutcome,
            TerminalReason,
            _transitions,
            _verdicts,
            _approvals,
            TemplateId,
            TemplateVersion,
            TemplateGraphSnapshotJson,
            TemplateSchemeSnapshotJson,
            artifacts);
    }

    /// <summary>
    /// Advances the run to the stage the active scheme declares as the next one. The transition is only
    /// allowed when the current stage's required artifact is a stored, run-scoped artifact of this run and
    /// this stage whose recorded hash is the one every required reviewer approved and the user approval
    /// pinned: an absent, foreign, wrong-kind or unverifiable artifact blocks the transition instead of
    /// being replaced by whatever hash the last recorded verdict happened to carry.
    /// <para>
    /// On a run pinned to a template version, every authorizing verdict must additionally be backed by a
    /// <see cref="ReviewerExecutionEvidence"/> supplied in <paramref name="reviewerExecutions"/>: a
    /// succeeded, read-only turn on this run, this stage and this role, whose observed route equals the
    /// route the verdict names and whose reviewed artifact is the very artifact the gate is about. An
    /// unlinked verdict, an execution of another run, a turn that was cancelled or ambiguous, a route that
    /// was requested but never observed and a verdict of a replaced artifact all block the transition.
    /// </para>
    /// <para>
    /// The evidence is passed in rather than read here so this aggregate stays free of storage: the run
    /// service resolves the live execution rows immediately before the transition, which is also what keeps
    /// a verdict from outliving the execution that produced it. A legacy, non-pinned run keeps the original
    /// rule, where a role, a route string and a hash were the whole of the evidence.
    /// </para>
    /// </summary>
    public WorkflowTransitionRecord AdvanceTo(
        WorkflowStageDefinition currentStage,
        WorkflowStageDefinition nextStage,
        string reason,
        DateTimeOffset triggeredAtUtc,
        IReadOnlyList<ReviewerExecutionEvidence>? reviewerExecutions = null)
    {
        ArgumentNullException.ThrowIfNull(currentStage);
        ArgumentNullException.ThrowIfNull(nextStage);
        EnsureNotTerminal("advance to another stage");
        if (IsTemplateBacked)
        {
            WorkflowPinnedStageContract.EnsureMatches(TemplateSchemeSnapshotJson!, currentStage);
            WorkflowPinnedStageContract.EnsureMatches(TemplateSchemeSnapshotJson!, nextStage);
        }

        if (!string.Equals(CurrentStageId, currentStage.StageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The run is at stage '{CurrentStageId}', not '{currentStage.StageId}'.");
        }

        if (!string.Equals(currentStage.NextStageId, nextStage.StageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stage '{currentStage.StageId}' does not declare '{nextStage.StageId}' as its next stage.");
        }

        var guardedReason = DomainGuard.NotBlank(reason, nameof(reason));

        if (triggeredAtUtc < StartedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triggeredAtUtc),
                "A transition cannot predate the workflow run.");
        }

        var gate = EvaluateTransitionGate(currentStage, reviewerExecutions);

        var transition = new WorkflowTransitionRecord(
            Guid.NewGuid().ToString("N"),
            currentStage.StageId,
            nextStage.StageId,
            triggeredAtUtc,
            guardedReason,
            gate.Verdicts,
            gate.Approval,
            gate.Artifact?.ArtifactId,
            gate.Artifact?.HashSha256);

        _transitions.Add(transition);
        CurrentStageId = nextStage.StageId;
        CurrentRole = nextStage.RequiredRole;

        return transition;
    }

    public void Complete(string reason, DateTimeOffset endedAtUtc) =>
        ApplyTerminal(
            WorkflowRunState.Completed,
            WorkflowTerminalOutcome.Completed,
            reason,
            endedAtUtc);

    public void Fail(string reason, DateTimeOffset endedAtUtc) =>
        ApplyTerminal(
            WorkflowRunState.Failed,
            WorkflowTerminalOutcome.Failed,
            reason,
            endedAtUtc);

    public void Cancel(string reason, DateTimeOffset endedAtUtc) =>
        ApplyTerminal(
            WorkflowRunState.Cancelled,
            WorkflowTerminalOutcome.Cancelled,
            reason,
            endedAtUtc);

    private void ApplyTerminal(
        WorkflowRunState state,
        WorkflowTerminalOutcome outcome,
        string reason,
        DateTimeOffset endedAtUtc)
    {
        EnsureNotTerminal($"become '{state}'");

        var guardedReason = DomainGuard.NotBlank(reason, nameof(reason));

        if (endedAtUtc < StartedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAtUtc),
                "A terminal workflow outcome cannot predate the workflow run.");
        }

        State = state;
        TerminalOutcome = outcome;
        EndedAtUtc = endedAtUtc;
        TerminalReason = guardedReason;
    }

    /// <summary>
    /// Checks aggregate gate invariants without changing this run. Application services must additionally
    /// establish any external response authority; supplied execution metadata alone is not model evidence.
    /// </summary>
    public void ValidateTransitionGate(WorkflowStageDefinition stage,
        IReadOnlyList<ReviewerExecutionEvidence>? reviewerExecutions = null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        EnsureNotTerminal("validate a transition gate");
        if (IsTemplateBacked)
            WorkflowPinnedStageContract.EnsureMatches(TemplateSchemeSnapshotJson!, stage);
        if (!string.Equals(CurrentStageId, stage.StageId, StringComparison.Ordinal))
            throw new InvalidOperationException("The transition gate must belong to the run's current stage.");
        EvaluateTransitionGate(stage, reviewerExecutions);
    }

    private (IReadOnlyList<ReviewerVerdictRecord> Verdicts, UserApprovalEvidence? Approval, WorkflowArtifactEvidence? Artifact)
        EvaluateTransitionGate(
            WorkflowStageDefinition stage,
            IReadOnlyList<ReviewerExecutionEvidence>? reviewerExecutions)
    {
        var declaresAHumanGate = stage.RequiredReviewerRoles.Count > 0 || stage.RequiresUserApproval;

        // A gate that has nothing to decide on is not a gate. Without a required artifact there is no
        // verified content to approve, and letting the stage declare reviewers or an approval anyway would
        // make the check pass against an arbitrary hash supplied by a verdict.
        if (declaresAHumanGate && stage.ArtifactRequirement is null)
        {
            throw new InvalidOperationException(
                $"Stage '{stage.StageId}' requires reviewers or a user approval but declares no artifact "
                    + "requirement, so there is no stored content the gate could be decided on. "
                    + "The transition is blocked.");
        }

        var artifact = stage.ArtifactRequirement is { } requiredKind
            ? ResolveCurrentArtifact(stage.StageId, requiredKind)
            : null;

        if (stage.ArtifactRequirement is not null && artifact is null)
        {
            throw new InvalidOperationException(
                $"Stage '{stage.StageId}' requires a stored '{stage.ArtifactRequirement}' artifact recorded for "
                    + "this run and this stage, and no such artifact exists. The transition is blocked.");
        }

        // The current hash is the hash of the stored artifact, never the hash of the last recorded verdict.
        // An approval-only stage therefore still has one, and a verdict on some other document cannot supply
        // a substitute for a missing one.
        var documentHash = artifact?.HashSha256;

        var authorizingVerdicts = new List<ReviewerVerdictRecord>(stage.RequiredReviewerRoles.Count);

        foreach (var role in stage.RequiredReviewerRoles)
        {
            var latestVerdict = _verdicts
                .Where(verdict => string.Equals(verdict.ReviewerRole, role, StringComparison.Ordinal)
                    && string.Equals(verdict.DocumentHash, documentHash, StringComparison.Ordinal))
                .OrderByDescending(verdict => verdict.RecordedAtUtc)
                .FirstOrDefault();

            if (latestVerdict is null)
            {
                throw new InvalidOperationException(
                    $"The required reviewer '{role}' has no verdict on document hash '{documentHash}'. "
                    + "The check is incomplete and the transition is blocked.");
            }

            if (latestVerdict.Verdict != WorkflowReviewVerdict.Approve)
            {
                throw new InvalidOperationException(
                    $"The reviewer '{role}' returned '{latestVerdict.Verdict}' on document hash '{documentHash}'. "
                    + "Only a unanimous 'Approve' allows the transition.");
            }

            EnsureVerdictIsBackedByAReviewerExecution(latestVerdict, role, artifact!, reviewerExecutions);

            authorizingVerdicts.Add(latestVerdict);
        }

        UserApprovalEvidence? approval = null;

        if (stage.RequiresUserApproval)
        {
            // The stage's latest approved decision is the one that counts, and it has to pin the current hash.
            // Looking the approval up by hash instead would let a later approval of a different document
            // coexist with an older approval of the current one and be ignored, and there is no null-hash way
            // in: an approval recorded against an earlier document stays in the history and authorizes nothing.
            var latestApproval = _approvals
                .Where(candidate => string.Equals(candidate.StageId, stage.StageId, StringComparison.Ordinal)
                    && candidate.Decision == UserApprovalDecision.Approved)
                .OrderByDescending(candidate => candidate.DecidedAtUtc)
                .FirstOrDefault();

            approval = latestApproval is not null
                && string.Equals(latestApproval.ArtifactHash, documentHash, StringComparison.Ordinal)
                ? latestApproval
                : null;

            if (approval is null)
            {
                throw new InvalidOperationException(
                    $"Stage '{stage.StageId}' requires an explicit user approval for document hash "
                    + $"'{documentHash ?? "<none>"}'. The transition is blocked.");
            }
        }

        return (authorizingVerdicts, approval, artifact);
    }

    /// <summary>
    /// Whether a pinned model-review stage may be authorized by this verdict, or the named reason it may not.
    /// <para>
    /// A run pinned to a template version is a production model-review run, and there an approval is a claim
    /// about a model only if a stored execution supports it. The record must therefore name an execution,
    /// that execution must be present in the evidence the caller resolved from storage, and it must describe
    /// the same run, stage, role, route and current verified artifact. This is also what makes an approval
    /// recorded before this rule existed unusable rather than quietly still valid: such a row is unlinked,
    /// and an unlinked row never reaches an approval.
    /// </para>
    /// <para>
    /// A legacy run - one that carries no template identity and follows the process-wide standard scheme -
    /// keeps the original rule, where the role, the route string and the hash were the whole of the
    /// evidence. That is a documented compatibility for runs that predate model review, and it is
    /// deliberately not extended to any run a pinned template governs.
    /// </para>
    /// </summary>
    private void EnsureVerdictIsBackedByAReviewerExecution(
        ReviewerVerdictRecord verdict,
        string role,
        WorkflowArtifactEvidence artifact,
        IReadOnlyList<ReviewerExecutionEvidence>? reviewerExecutions)
    {
        if (!IsTemplateBacked)
        {
            return;
        }

        if (verdict.ExecutionId is not { } executionId)
        {
            throw new InvalidOperationException(
                $"The reviewer '{role}' approved document hash '{artifact.HashSha256}' with a verdict that names "
                    + "no reviewer execution. A pinned model-review stage is authorized only by a persisted, "
                    + "read-only reviewer execution whose observed route matches the assigned route, so the "
                    + "transition is blocked.");
        }

        var evidence = reviewerExecutions?
            .FirstOrDefault(candidate => string.Equals(candidate.ExecutionId, executionId, StringComparison.Ordinal));

        if (evidence is null)
        {
            throw new InvalidOperationException(
                $"The reviewer '{role}' approved document hash '{artifact.HashSha256}' with verdict "
                    + $"'{executionId}', but no persisted reviewer execution of this run carries that id. "
                    + "A cancelled, ambiguous or lost execution authorizes nothing, and the transition is "
                    + "blocked.");
        }

        var unsatisfied = evidence.FindUnsatisfiedReason(Id, artifact.StageId, role, artifact, verdict);

        if (unsatisfied is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The reviewer '{role}' approved document hash '{artifact.HashSha256}' with verdict "
                + $"'{executionId}', but {unsatisfied}. The transition is blocked.");
    }

    /// <summary>
    /// The stored artifact that currently authorizes a stage: the newest one recorded for this run, this
    /// stage and exactly the kind the stage requires. A newer artifact of the same kind recorded for another
    /// stage - including one produced by a failure loop - is that other stage's evidence and is never used
    /// here.
    /// </summary>
    private WorkflowArtifactEvidence? ResolveCurrentArtifact(string stageId, string kind) =>
        WorkflowArtifactEvidence.SelectCurrent(_artifacts, Id, stageId, kind);

    private void EnsureNotTerminal(string operation)
    {
        if (!IsTerminal)
        {
            return;
        }

        throw new InvalidStateTransitionException(
            nameof(WorkflowRun),
            State,
            TerminalOutcome,
            $"the run is already terminal and cannot {operation}");
    }

    /// <summary>
    /// The assigned template identity and its two snapshots are one value: a run either carries all four
    /// or none of them. A half-pinned run would advance against a scheme nobody recorded, so it is refused
    /// here as well as by the storage layer.
    /// </summary>
    private void EnsureTemplateIdentityIsWhole()
    {
        var recorded = new[]
        {
            TemplateId,
            TemplateVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TemplateGraphSnapshotJson,
            TemplateSchemeSnapshotJson
        };

        var present = recorded.Count(value => value is not null);

        if (present != 0 && present != recorded.Length)
        {
            throw new ArgumentException(
                "A workflow run must record its assigned template identity and both template snapshots "
                    + "together, or none of them.",
                nameof(TemplateId));
        }

        if (present == recorded.Length && TemplateVersion is { } version && version < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TemplateVersion),
                version,
                "A pinned workflow run must name a template version greater than zero.");
        }
    }

    /// <summary>
    /// Every recorded artifact is evidence of one run. A row that names another run is refused at
    /// construction rather than being quietly skipped later, because a run that carries a foreign artifact
    /// could be advanced on evidence that was never produced for it.
    /// </summary>
    private void EnsureArtifactsBelongToThisRun()
    {
        foreach (var artifact in _artifacts)
        {
            if (!string.Equals(artifact.RunId, Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The artifact '{artifact.ArtifactId}' belongs to run '{artifact.RunId}', not to run '{Id}'.",
                    nameof(_artifacts));
            }
        }
    }

    private void EnsureChronology()
    {        if (EndedAtUtc is { } endedAt && endedAt < StartedAtUtc)
        {
            throw new ArgumentException(
                "EndedAtUtc cannot be earlier than StartedAtUtc.",
                nameof(EndedAtUtc));
        }
    }

    private void EnsureTerminalConsistency()
    {
        if (IsTerminal)
        {
            if (EndedAtUtc is null)
            {
                throw new ArgumentException(
                    "A terminal workflow run must record EndedAtUtc.",
                    nameof(EndedAtUtc));
            }

            if (TerminalOutcome == WorkflowTerminalOutcome.None)
            {
                throw new ArgumentException(
                    "A terminal workflow run must record a terminal outcome other than 'None'.",
                    nameof(TerminalOutcome));
            }

            if (!IsOutcomeCompatibleWithState(State, TerminalOutcome))
            {
                throw new ArgumentException(
                    $"Terminal outcome '{TerminalOutcome}' is not compatible with state '{State}'.",
                    nameof(TerminalOutcome));
            }

            return;
        }

        if (EndedAtUtc is not null)
        {
            throw new ArgumentException(
                "A non-terminal workflow run cannot record EndedAtUtc.",
                nameof(EndedAtUtc));
        }

        if (TerminalOutcome != WorkflowTerminalOutcome.None)
        {
            throw new ArgumentException(
                "A non-terminal workflow run cannot record a terminal outcome.",
                nameof(TerminalOutcome));
        }

        if (TerminalReason is not null)
        {
            throw new ArgumentException(
                "A non-terminal workflow run cannot record a terminal reason.",
                nameof(TerminalReason));
        }
    }

    private static bool IsOutcomeCompatibleWithState(
        WorkflowRunState state,
        WorkflowTerminalOutcome outcome) =>
        state switch
        {
            WorkflowRunState.Completed => outcome == WorkflowTerminalOutcome.Completed,
            WorkflowRunState.Failed => outcome is WorkflowTerminalOutcome.Failed or WorkflowTerminalOutcome.Rejected,
            WorkflowRunState.Cancelled => outcome == WorkflowTerminalOutcome.Cancelled,
            _ => false
        };
}
