using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

public sealed partial class WorkflowRunService : IWorkflowRunService
{
    private readonly IWorkflowRunRepository _repository;
    private readonly WorkflowScheme _scheme;
    private readonly WorkflowTemplateExecutionPlanResolver _planResolver;
    private readonly IWorkflowArtifactBlobStore _artifactBlobStore;
    private readonly IUserApprovalIdentity _userApprovalIdentity;
    private readonly IWorkflowReviewEvidenceRepository? _reviewEvidenceRepository;
    private readonly TimeProvider _timeProvider;
    // Production resolves one service instance. Keep one bounded writer gate across aggregate
    // read/validate/write commands; none of these commands waits for a native model response.
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public WorkflowRunService(
        IWorkflowRunRepository repository,
        WorkflowScheme scheme,
        IWorkflowTemplateStore templateStore,
        IWorkflowArtifactBlobStore artifactBlobStore,
        IUserApprovalIdentity userApprovalIdentity,
        TimeProvider? timeProvider = null,
        IWorkflowReviewEvidenceRepository? reviewEvidenceRepository = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(artifactBlobStore);
        ArgumentNullException.ThrowIfNull(userApprovalIdentity);

        _repository = repository;
        _scheme = scheme;
        _planResolver = new WorkflowTemplateExecutionPlanResolver(templateStore);
        _artifactBlobStore = artifactBlobStore;
        _userApprovalIdentity = userApprovalIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // The reviewer-execution evidence is optional in the constructor and deliberately so. A composition
        // that does not supply it can still run every other command, and it simply cannot record or satisfy
        // a linked model-review verdict - which fails closed. Making it a hard requirement instead would
        // turn "this build has no reviewer evidence store" into "this build cannot start".
        _reviewEvidenceRepository = reviewEvidenceRepository;
    }

    public async Task<WorkflowRun> StartRunAsync(
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(projectId, nameof(projectId));
        ApplicationGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        ApplicationGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));

        // The assignment is resolved, validated and refused before a run object exists, so an unusable
        // template never leaves a partially started run behind.
        var plan = await _planResolver
            .ResolveForProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        var initialStage = plan.Scheme.Scheme.GetRequiredStage(plan.Scheme.Scheme.InitialStageId);

        var run = WorkflowRun.StartPinnedToTemplate(
            Guid.NewGuid().ToString("N"),
            projectId,
            workflowPackageId,
            workflowVersionId,
            sessionId,
            initialStage,
            _timeProvider.GetUtcNow(),
            plan.TemplateId,
            plan.TemplateVersion,
            plan.GraphSnapshotJson,
            plan.SchemeSnapshotJson);

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    public async Task<WorkflowRun> StartLegacyRunAsync(
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(projectId, nameof(projectId));
        ApplicationGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        ApplicationGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));

        var initialStage = _scheme.GetRequiredStage(_scheme.InitialStageId);

        var run = WorkflowRun.Start(
            Guid.NewGuid().ToString("N"),
            projectId,
            workflowPackageId,
            workflowVersionId,
            sessionId,
            initialStage,
            _timeProvider.GetUtcNow());

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// Validates reviewer dispatch metadata, then refuses absent persisted response/parser authority.
    /// <para>
    /// This is the authority path, and it is checked rather than trusted. The verdict must name the
    /// execution it was read out of; that execution must exist; it must be a succeeded, read-only turn of
    /// this run, on the stage the run currently sits on, for the role the verdict names, whose observed
    /// route equals the route the verdict names and whose reviewed artifact is the run's newest stored
    /// artifact of the kind that stage requires. Everything that arrives with the verdict - the role, the
    /// route string, the hash, the verdict itself - is compared against stored rows rather than believed.
    /// </para>
    /// <para>
    /// A verdict for a replaced artifact, a verdict of another run, a cancelled or ambiguous turn, a route
    /// that was requested but never observed and a verdict with no execution at all are all refused here by
    /// name, so an operator click, a recovery scenario and any other caller are equally unable to write
    /// model-review evidence. Matching metadata remains insufficient: bounded response capture and parsing
    /// do not provide trusted per-turn native response origin. The explicitly legacy, non-model path is
    /// <see cref="RecordLegacyUnlinkedReviewerVerdictAsync"/>, and it is refused for a pinned run.
    /// </para>
    /// </summary>
    public async Task<WorkflowRun> RecordReviewerVerdictAsync(
        string runId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        if (run.IsTerminal)
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is already terminal ({run.State}) and cannot record a reviewer verdict.");
        }

        if (!verdict.IsLinked)
        {
            throw new WorkflowValidationException(
                $"The reviewer verdict for role '{verdict.ReviewerRole}' names no reviewer execution. A verdict "
                    + "that is not read out of a persisted execution is not model-review evidence, so it is not "
                    + "recorded. On a run without a pinned template the explicitly legacy path "
                    + "(RecordLegacyUnlinkedReviewerVerdictAsync) is available instead.");
        }

        var evidence = _reviewEvidenceRepository is null
            ? null
            : await _reviewEvidenceRepository
                .GetByExecutionIdAsync(verdict.ExecutionId!, cancellationToken)
                .ConfigureAwait(false);

        if (evidence is null)
        {
            throw new WorkflowValidationException(
                $"The reviewer verdict for role '{verdict.ReviewerRole}' names execution "
                    + $"'{verdict.ExecutionId}', and no persisted reviewer execution of run '{run.Id}' carries "
                    + "that id. A cancelled, ambiguous or lost execution authorizes nothing, so no verdict is "
                    + "recorded.");
        }

        var current = await RequireReviewableArtifactAsync(run, verdict, cancellationToken).ConfigureAwait(false);

        var unsatisfied = evidence.FindUnsatisfiedReason(
            run.Id,
            current.StageId,
            verdict.ReviewerRole,
            current,
            verdict);

        if (unsatisfied is not null)
        {
            throw new WorkflowValidationException(
                $"The reviewer verdict for role '{verdict.ReviewerRole}' on document hash "
                    + $"'{verdict.DocumentHash}' is refused: {unsatisfied}. No verdict is recorded.");
        }

        // Dispatch metadata establishes which artifact a turn addressed, not what the model answered.
        // A persisted bounded answer/parser candidate is transport evidence. No composed production
        // channel supplies trusted per-turn native origin, so caller enums/text cannot gain authority.
        throw new WorkflowValidationException(WorkflowValidationFailure.MissingModelResponse, "The reviewer execution has no authorizing model response bound to trusted per-turn native origin. " +
            "Persisted text, parser output and dispatch metadata alone cannot authorize a caller-supplied verdict; no verdict was recorded.");
    }

    /// <summary>
    /// Records an unlinked verdict on the explicitly legacy, non-model path.
    /// <para>
    /// This is the only way a role, a route string and a hash can become a stored verdict without a
    /// reviewer execution behind them, and it exists so a run that predates template pinning and the history
    /// already recorded on such runs stay readable. It is refused for a run pinned to a template version,
    /// so it can never satisfy a production model-review stage, and the aggregate refuses the same case
    /// again so the rule does not depend on this method being the only door.
    /// </para>
    /// </summary>
    public async Task<WorkflowRun> RecordLegacyUnlinkedReviewerVerdictAsync(
        string runId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(verdict);

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        if (run.IsTemplateBacked)
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is pinned to template '{run.TemplateId}' version "
                    + $"{run.TemplateVersion}, so it refuses a legacy unlinked reviewer verdict. Only a verdict "
                    + "read out of a persisted reviewer execution can authorize a pinned model-review stage.");
        }

        run.RecordLegacyUnlinkedReviewerVerdict(verdict);

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// The run's own newest stored artifact of the kind its current stage requires, re-hashed, and only when
    /// it is the artifact the verdict claims to be about.
    /// <para>
    /// The stage is resolved against the run's pinned scheme rather than from the verdict, the kind comes
    /// from that stage rather than from the caller, and the artifact is the newest one recorded for this
    /// run, this stage and that kind. The verdict's own stage, hash and artifact id are then compared
    /// exactly against it, so a superseded hash and a replaced artifact row are refused here rather than
    /// only at the gate - and the bytes are re-hashed first, so a row whose blob is gone is not evidence
    /// either.
    /// </para>
    /// </summary>
    private async Task<WorkflowArtifactEvidence> RequireReviewableArtifactAsync(
        WorkflowRun run,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken)
    {
        if (verdict.StageId is not { } verdictStageId
            || !string.Equals(run.CurrentStageId, verdictStageId, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is at stage '{run.CurrentStageId}', so a reviewer verdict for the "
                    + $"stage '{verdict.StageId ?? "<none>"}' is refused.");
        }

        var stage = ResolveScheme(run).FindStage(verdictStageId)
            ?? throw new WorkflowValidationException(
                $"Stage '{verdictStageId}' is not declared by the scheme the run is decided against.");

        if (stage.ArtifactRequirement is not { } requiredKind)
        {
            throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' declares no artifact requirement, so there is no stored content a "
                    + "reviewer verdict could be about.");
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stage.StageId, requiredKind)
            ?? throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' requires a stored '{requiredKind}' artifact recorded for this run and "
                    + "this stage, and no such artifact exists, so no reviewer verdict is recorded.");

        if (!await _artifactBlobStore.VerifyAsync(current.BlobId, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowValidationException(
                $"The stored '{requiredKind}' artifact '{current.ArtifactId}' of stage '{stage.StageId}' is no "
                    + "longer present with the bytes it was recorded with, so it cannot be reviewed.");
        }

        if (!string.Equals(current.HashSha256, verdict.DocumentHash, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The current '{requiredKind}' artifact of stage '{stage.StageId}' has hash "
                    + $"'{current.HashSha256}', not '{verdict.DocumentHash}', so the verdict names a document "
                    + "that is no longer current.");
        }

        if (verdict.ReviewedArtifactId is { } reviewedArtifactId
            && !string.Equals(current.ArtifactId, reviewedArtifactId, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The current '{requiredKind}' artifact of stage '{stage.StageId}' is row "
                    + $"'{current.ArtifactId}', not '{reviewedArtifactId}', so the verdict names a record that "
                    + "has been replaced.");
        }

        return current;
    }

    public async Task<WorkflowRun> RecordUserApprovalAsync(
        string runId,
        UserApprovalEvidence approval,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(approval);

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        if (run.IsTerminal)
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is already terminal ({run.State}) and cannot record a user approval.");
        }

        var current = await RequireApprovableArtifactAsync(run, approval.StageId, approval.ArtifactHash, cancellationToken)
            .ConfigureAwait(false);

        // The local approver and decision time are resolved here. Neither a caller-supplied principal
        // nor a caller-supplied timestamp becomes the audit claim for this approval.
        var stamped = new UserApprovalEvidence(
            approval.ApprovalId,
            ResolveApproverIdentity(),
            approval.StageId,
            current.HashSha256,
            approval.Decision,
            approval.Comment,
            _timeProvider.GetUtcNow());

        run.RecordUserApproval(stamped);

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// The stored artifact an approval may be recorded against, or the named reason there is none.
    ///
    /// Nothing the caller supplied is trusted. The stage is resolved against the run's own scheme and has
    /// to be the stage the run actually sits on; the kind comes from that stage's artifact requirement and
    /// is never taken from the caller; the artifact is the newest one recorded for this run, this stage and
    /// that kind; and the bytes behind it are re-hashed here, immediately before the decision is stored.
    /// Only then is the caller's hash compared against it, exactly, so a stale hash, a hash of a document
    /// that was replaced and a hash of bytes that no longer exist are all refused by name.
    /// </summary>
    private async Task<WorkflowArtifactEvidence> RequireApprovableArtifactAsync(
        WorkflowRun run,
        string approvalStageId,
        string approvalHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(run.CurrentStageId, approvalStageId, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is at stage '{run.CurrentStageId}', so an approval for the stage "
                    + $"'{approvalStageId}' is refused.");
        }

        var stage = ResolveScheme(run).FindStage(approvalStageId)
            ?? throw new WorkflowValidationException(
                $"Stage '{approvalStageId}' is not declared by the scheme the run is decided against.");

        if (stage.ArtifactRequirement is not { } requiredKind)
        {
            throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' declares no artifact requirement, so there is no stored content an "
                    + "approval could be about.");
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stage.StageId, requiredKind)
            ?? throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' requires a stored '{requiredKind}' artifact recorded for this run and "
                    + "this stage, and no such artifact exists, so no approval is recorded.");

        if (!await _artifactBlobStore.VerifyAsync(current.BlobId, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowValidationException(
                $"The stored '{requiredKind}' artifact '{current.ArtifactId}' of stage '{stage.StageId}' is no "
                    + "longer present with the bytes it was recorded with, so it cannot be approved.");
        }

        if (!string.Equals(current.HashSha256, approvalHash, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The current '{requiredKind}' artifact of stage '{stage.StageId}' has hash '{current.HashSha256}', "
                    + $"not '{approvalHash}', so the approval names a document that is no longer current.");
        }

        return current;
    }

    /// <summary>
    /// The identity the approval is stamped with, or a refusal. There is no fallback name: an unavailable
    /// identity means no approval is recorded, because an approval nobody can be named for would be an
    /// approval of an unknown person.
    /// </summary>
    private string ResolveApproverIdentity()
    {
        var identity = _userApprovalIdentity.GetCurrentApproverIdentity();

        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new WorkflowValidationException(
                "The local Windows logon identity of this window could not be established, so no user approval "
                    + "is recorded.");
        }

        return identity;
    }

    public async Task<WorkflowRun> AdvanceStageAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var scheme = ResolveScheme(run);

        var currentStage = scheme.FindStage(run.CurrentStageId)
            ?? throw new WorkflowValidationException(
                $"Stage '{run.CurrentStageId}' is not declared by the scheme the run is advanced against.");

        if (currentStage.NextStageId is null)
        {
            throw new WorkflowValidationException(
                $"Stage '{currentStage.StageId}' is a terminal stage; use a terminal transition instead.");
        }

        // Only the success target is ever followed. A stage that also declares a failure target keeps that
        // edge on its pinned scheme and gains nothing from it here: when the gate below refuses - a reviewer
        // verdict that is not Approve, a missing artifact, a missing approval - this throws and the run
        // stays on the stage whose gate refused it. It is never moved along the failure target, and above
        // all it is never moved to the success target as if the gate had passed.
        var nextStage = scheme.GetRequiredStage(currentStage.NextStageId);

        // The row is not the evidence: the bytes are. A run that was advanced once and then reopened can
        // carry a perfectly complete artifact row whose file was deleted or rewritten in between, so the
        // stored bytes are re-hashed here, immediately before the gate is evaluated.
        await EnsureCurrentArtifactIsStillStoredAsync(run, currentStage, cancellationToken)
            .ConfigureAwait(false);

        // The reviewer executions are read here, from storage, at the moment of the transition. They are
        // deliberately not part of the run's own evidence payload: a verdict must stop authorizing the
        // moment its execution stops being a succeeded, read-only, correctly-routed turn of this artifact,
        // and a snapshot taken when the verdict was written cannot express that.
        var reviewerExecutions = _reviewEvidenceRepository is null
            ? Array.Empty<ReviewerExecutionEvidence>()
            : (await _reviewEvidenceRepository
                .ListByRunIdAsync(run.Id, cancellationToken)
                .ConfigureAwait(false)).ToArray();

        if (run.IsTemplateBacked && currentStage.RequiredReviewerRoles.Count > 0)
        {
            // Preserve precise missing/stale/rejected metadata diagnostics, but do not let a historical
            // caller-authored Approve become authoritative merely because those checks all pass.
            run.ValidateTransitionGate(currentStage, reviewerExecutions);
            throw new WorkflowValidationException(WorkflowValidationFailure.MissingModelResponse, "The pinned model-review stage has no persisted, hash-bound model response and parser-produced " +
                "verdict evidence. Historical caller-supplied approvals and dispatch metadata cannot advance it.");
        }

        var expected = run.Snapshot();
        run.AdvanceTo(currentStage, nextStage, reason, _timeProvider.GetUtcNow(), reviewerExecutions);

        await _repository.SaveTransitionAsync(expected, run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// Refuses a failure transition by name, and never performs one.
    /// <para>
    /// A stage's failure target is preserved on the run - in the pinned graph, as the stage's own
    /// <see cref="WorkflowStageDefinition.FailureStageId"/> and therefore in the pinned scheme snapshot that
    /// survives a restart - and this method is the only way to reach it. It never reaches it, because the
    /// aggregate has no transition that is distinguished from a success one: <see cref="WorkflowRun.AdvanceTo"/>
    /// authorizes a move to the stage a stage names as its next, and a reviewer verdict that is not
    /// <c>Approve</c> is refused there instead of being rerouted. Inventing a reroute rule here would be a
    /// new safety rule with no evidence behind it, and a backward-pointing failure edge with no declared
    /// budget would loop without end.
    /// </para>
    /// <para>
    /// So the attempt is refused, out loud, with the run, its current stage and the declared target named.
    /// Nothing is written: the stage does not move, no transition is recorded and no terminal outcome is
    /// set, which is also what keeps a node's execution status from being read as a workflow outcome.
    /// </para>
    /// </summary>
    public async Task<WorkflowRun> AdvanceToDeclaredFailureStageAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(runId, nameof(runId));
        ApplicationGuard.NotBlank(reason, nameof(reason));

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var stage = ResolveScheme(run).FindStage(run.CurrentStageId)
            ?? throw new WorkflowValidationException(
                $"[{WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute}] The workflow run '{run.Id}' is at "
                    + $"stage '{run.CurrentStageId}', and that stage is not declared by the scheme the run is "
                    + "decided against, so it has no failure route to take.");

        if (run.IsTerminal)
        {
            throw new WorkflowValidationException(
                $"[{WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute}] The workflow run '{run.Id}' is "
                    + $"already terminal ({run.State}) and cannot leave stage '{stage.StageId}'.");
        }

        if (stage.FailureStageId is not { } failureStageId)
        {
            throw new WorkflowValidationException(
                $"[{WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute}] The workflow run '{run.Id}' is at "
                    + $"stage '{stage.StageId}', which declares no failure target in the scheme the run is "
                    + "pinned to, so there is no failure route to take.");
        }

        throw new WorkflowValidationException(
            $"[{WorkflowRunFailureRouteRefusals.FailureRouteNotExecutable}] The workflow run '{run.Id}' is at "
                + $"stage '{stage.StageId}', which declares the failure target '{failureStageId}'. That edge is "
                + "preserved on the run and in its pinned scheme, and this build cannot take it: no evidence a "
                + "run holds authorizes leaving a stage along its failure route, so the run stays where it is "
                + "rather than being moved there or being reported as moved to the success target. Use the "
                + "terminal fail command to end the run with a named outcome.");
    }

    public async Task<WorkflowRun> RecordStageArtifactAsync(
        string runId,
        string stageId,
        string kind,
        Stream content,
        DataClassification classification,
        CancellationToken cancellationToken = default) =>
        (await RecordArtifactCoreAsync(runId, stageId, kind, null, content, classification, cancellationToken)
            .ConfigureAwait(false)).Run;

    public Task<WorkflowArtifactRecordingResult> RecordExecutionArtifactAsync(string runId, string stageId, string kind,
        string executionId, Stream content, DataClassification classification,
        CancellationToken cancellationToken = default) =>
        RecordArtifactCoreAsync(runId, stageId, kind, ApplicationGuard.NotBlank(executionId, nameof(executionId)),
            content, classification, cancellationToken);

    private async Task<WorkflowArtifactRecordingResult> RecordArtifactCoreAsync(string runId, string stageId, string kind,
        string? executionId, Stream content, DataClassification classification, CancellationToken cancellationToken)
    {
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(content);
        if (!Enum.IsDefined(classification)) throw new ArgumentOutOfRangeException(nameof(classification));

        var guardedStageId = ApplicationGuard.NotBlank(stageId, nameof(stageId));
        var guardedKind = ApplicationGuard.NotBlank(kind, nameof(kind));

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var stage = ResolveRecordableStage(run, guardedStageId, guardedKind);

        if (run.IsTemplateBacked)
        {
            var node = WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id).Nodes
                .Single(n => n.NodeId == stage.StageId);
            if (node.Kind == WorkflowNodeKind.ArtifactCollection
                && (executionId is null || node.ArtifactContract != guardedKind))
                throw new WorkflowValidationException("ArtifactCollection requires its pinned kind and a verified execution association.");
        }

        if (executionId is not null)
            await _repository.ValidateArtifactExecutionAsync(run, executionId, cancellationToken).ConfigureAwait(false);

        // The bytes come first: a blob that was never written can never be named by a row, and a row that
        // names a blob that was never written would authorize a transition against nothing.
        // Every artifact path shares the ingest bound, including ordinary document stages that have
        // no native execution association. Refuse before either the blob or evidence row is written.
        using var captured = await CaptureArtifactAsync(content, cancellationToken).ConfigureAwait(false);
        var blob = await _artifactBlobStore
            .SaveAsync(captured, cancellationToken)
            .ConfigureAwait(false);

        var evidence = new WorkflowArtifactEvidence(
            Guid.NewGuid().ToString("N"),
            run.Id,
            stage.StageId,
            stage.ArtifactRequirement!,
            blob.BlobId,
            blob.BlobId,
            _timeProvider.GetUtcNow(),
            blob.SizeBytes,
            classification,
            executionId);

        // The artifact lands on a copy. If the insert fails, the run the caller still holds is the one
        // without the evidence, so nothing in memory can advance on a row that was never committed.
        var staged = run.WithArtifact(evidence);

        await _repository.SaveArtifactAsync(staged, evidence, cancellationToken).ConfigureAwait(false);

        return new WorkflowArtifactRecordingResult(staged, evidence);
    }

    public Task<WorkflowRun> CancelRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ApplyTerminalAsync(
            runId,
            reason,
            static (run, guardedReason, now) => run.Cancel(guardedReason, now),
            cancellationToken);

    public Task<WorkflowRun> FailRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ApplyTerminalAsync(
            runId,
            reason,
            static (run, guardedReason, now) => run.Fail(guardedReason, now),
            cancellationToken);

    public Task<WorkflowRun> CompleteRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ApplyTerminalAsync(
            runId,
            reason,
            static (run, guardedReason, now) => run.Complete(guardedReason, now),
            cancellationToken,
            validateCompletion: true);

    private async Task<WorkflowRun> ApplyTerminalAsync(
        string runId,
        string reason,
        Action<WorkflowRun, string, DateTimeOffset> apply,
        CancellationToken cancellationToken,
        bool validateCompletion = false)
    {
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        var guardedReason = ApplicationGuard.NotBlank(reason, nameof(reason));

        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);

        if (validateCompletion && run.IsTemplateBacked)
        {
            var stage = ResolveScheme(run).FindStage(run.CurrentStageId)
                ?? throw new WorkflowValidationException("The current stage is absent from the pinned scheme.");
            if (stage.NextStageId is not null)
            {
                throw new WorkflowValidationException(
                    $"The pinned run must reach its terminal stage before successful completion; "
                    + $"stage '{stage.StageId}' still declares a next stage.");
            }

            await EnsureCurrentArtifactIsStillStoredAsync(run, stage, cancellationToken).ConfigureAwait(false);
            var reviewerExecutions = _reviewEvidenceRepository is null
                ? Array.Empty<ReviewerExecutionEvidence>()
                : (await _reviewEvidenceRepository.ListByRunIdAsync(run.Id, cancellationToken).ConfigureAwait(false)).ToArray();
            run.ValidateTransitionGate(stage, reviewerExecutions);
            if (stage.RequiredReviewerRoles.Count > 0)
            {
                throw new WorkflowValidationException(WorkflowValidationFailure.MissingModelResponse, "The pinned model-review stage has no persisted, hash-bound model response and parser-produced verdict evidence.");
            }
        }

        apply(run, guardedReason, _timeProvider.GetUtcNow());

        await _repository.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    private async Task<IDisposable> EnterMutationAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MutationLease(_mutationGate);
    }

    private sealed class MutationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task<WorkflowRun> RequireRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ApplicationGuard.NotBlank(runId, nameof(runId));

        return await _repository
            .GetByIdAsync(runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException($"The workflow run '{runId}' does not exist.");
    }

    /// <summary>
    /// The stage an artifact may be recorded for, resolved against the run's own scheme. A stage that is not
    /// the run's current stage is stale - the run already left it, and an artifact written against it could
    /// only ever be evidence of a document nobody is waiting for. A kind that is not exactly what the stage
    /// declares is refused too, because the gate only ever looks for that one kind and a second one would be
    /// recorded evidence that can never authorize anything.
    /// </summary>
    private WorkflowStageDefinition ResolveRecordableStage(
        WorkflowRun run,
        string stageId,
        string kind)
    {
        if (run.IsTerminal)
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is terminal and cannot record a stage artifact.");
        }

        if (!string.Equals(run.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The workflow run '{run.Id}' is at stage '{run.CurrentStageId}', so an artifact for the stale "
                    + $"stage '{stageId}' is refused.");
        }

        var stage = ResolveScheme(run).FindStage(stageId)
            ?? throw new WorkflowValidationException(
                $"Stage '{stageId}' is not declared by the scheme the run is advanced against.");

        if (stage.ArtifactRequirement is null)
        {
            throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' declares no artifact requirement, so it has nothing to record.");
        }

        if (!string.Equals(stage.ArtifactRequirement, kind, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"Stage '{stage.StageId}' requires an artifact of kind '{stage.ArtifactRequirement}', not "
                    + $"'{kind}'.");
        }

        return stage;
    }

    /// <summary>
    /// Re-checks the bytes behind the artifact the gate would authorize this stage with. A row that is
    /// complete in SQL but whose blob is missing, truncated or rewritten is not evidence, and refusing it
    /// here - before the aggregate is asked to decide - is what keeps a SQL-complete row from reaching
    /// <c>AdvanceTo</c> as current evidence.
    /// </summary>
    private async Task EnsureCurrentArtifactIsStillStoredAsync(
        WorkflowRun run,
        WorkflowStageDefinition currentStage,
        CancellationToken cancellationToken)
    {
        if (currentStage.ArtifactRequirement is not { } requiredKind)
        {
            return;
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(
            run.Artifacts,
            run.Id,
            currentStage.StageId,
            requiredKind);

        if (current is null)
        {
            // Nothing to re-verify. The aggregate refuses the transition with the name of the artifact that
            // is missing, which is the more useful message than a failure to verify nothing.
            return;
        }

        if (run.IsTemplateBacked && WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id)
                .GetRequiredNode(currentStage.StageId).Kind == WorkflowNodeKind.ArtifactCollection
            && current.ExecutionId is null)
            throw new WorkflowValidationException("The collected artifact has no execution association.");
        if (current.ExecutionId is { } executionId)
            await _repository.ValidateArtifactExecutionAsync(run, executionId, cancellationToken).ConfigureAwait(false);

        if (await _artifactBlobStore.VerifyAsync(current.BlobId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The stored '{requiredKind}' artifact '{current.ArtifactId}' of stage '{currentStage.StageId}' is "
            + "no longer present with the bytes it was recorded with, so it cannot authorize the transition.");
    }

    /// <summary>
    /// The scheme a run is advanced against.
    ///
    /// A template-backed run is advanced against the execution scheme snapshot it was pinned to, rebuilt
    /// from its own row, so the process-wide scheme cannot decide which stage comes next, which reviewers
    /// are required or whether a user approval is needed. A legacy run - one created before template
    /// pinning, or through the explicit legacy start - carries no snapshot and keeps following the
    /// process-wide standard scheme.
    /// </summary>
    private WorkflowScheme ResolveScheme(WorkflowRun run)
    {
        if (!run.IsTemplateBacked)
        {
            return _scheme;
        }

        return WorkflowSchemeSnapshot
            .Deserialize(run.TemplateSchemeSnapshotJson!, run.Id)
            .Scheme;
    }
}
