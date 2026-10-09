using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// Requests the assigned model review of a run's current stage artifact and persists only what a real
/// dispatch boundary can honestly produce.
/// <para>
/// Admission is per reviewer role. Preflight refusals write no rows for that role: the run must be a live
/// pinned run, the stage must come from its own pinned scheme, the roles
/// must come from that stage, the route for each role must come from the pinned template's own role binding
/// and must be a persisted <c>Routes</c> row with a resolvable account, profile and model, and the bytes
/// must be re-hashed and opened under a bound. Only then is a session and an execution persisted, and only
/// with a requested route. Production commits the session, execution and binding together before dispatch.
/// A crash after admission can leave a queued, non-authorizing execution. Other roles may already have
/// dispatched when a later role refuses or storage fails; the whole request is not one transaction.
/// </para>
/// <para>
/// After the dispatch, only what a backend reported is written to the observed-route column, and the
/// execution state is what the channel returned. A refusal to resolve a channel, a cancellation, an
/// exception of any kind and a route that came back as something other than what was asked for are all
/// terminal states that satisfy nothing. There is no automatic retry: a new explicit request may retry
/// a known Failed/Cancelled outcome, while an ambiguous delivery stays blocked until resolved.
/// </para>
/// </summary>
public sealed class WorkflowReviewRequestService : IWorkflowReviewRequestService
{
    /// <summary>
    /// The largest verified artifact a reviewer turn will read, at most 1 MiB.
    /// <para>
    /// A content hash proves authenticity and never a size, so an unbounded read would let one stored
    /// artifact turn one button press into an unbounded prompt. The bound is deliberately far below the
    /// 20 MiB the stage-artifact attach allows: reviewing a document is not the same operation as storing
    /// one, and a reviewer that cannot be shown the whole document is refused by name rather than shown a
    /// prefix of it.
    /// </para>
    /// </summary>
    public const long MaxReviewArtifactBytes = 1024L * 1024L;

    /// <summary>
    /// The capability a reviewer channel must declare. It is the only thing asked for, and a channel that
    /// does not declare it is not a reviewer: the product refuses rather than dispatching a turn to
    /// something that cannot be shown the artifact at all.
    /// </summary>
    public const string ReviewerCapability = "workflow.review.readonly";

    private readonly IWorkflowRunRepository _runRepository;
    private readonly IWorkflowTemplateStore _templateStore;
    private readonly IWorkflowReviewEvidenceRepository _evidenceRepository;
    private readonly IRouteRepository _routeRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly IWorkflowChannelCatalog _channelCatalog;
    private readonly IWorkflowArtifactBlobStore _artifactBlobStore;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowReviewDispatchStore? _dispatchStore;
    private readonly IDataClassificationGate? _dataClassificationGate;
    private readonly IWorkflowSecretScanner? _secretScanner;
    private readonly SemaphoreSlim _fallbackAdmissionGate = new(1, 1);

    public WorkflowReviewRequestService(
        IWorkflowRunRepository runRepository,
        IWorkflowTemplateStore templateStore,
        IWorkflowReviewEvidenceRepository evidenceRepository,
        IRouteRepository routeRepository,
        IProjectRepository projectRepository,
        ISessionRepository sessionRepository,
        IExecutionRepository executionRepository,
        IWorkflowChannelCatalog channelCatalog,
        IWorkflowArtifactBlobStore artifactBlobStore,
        TimeProvider? timeProvider = null,
        IWorkflowReviewDispatchStore? dispatchStore = null,
        IDataClassificationGate? dataClassificationGate = null,
        IWorkflowSecretScanner? secretScanner = null)
    {
        _runRepository = runRepository ?? throw new ArgumentNullException(nameof(runRepository));
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _evidenceRepository = evidenceRepository ?? throw new ArgumentNullException(nameof(evidenceRepository));
        _routeRepository = routeRepository ?? throw new ArgumentNullException(nameof(routeRepository));
        _projectRepository = projectRepository ?? throw new ArgumentNullException(nameof(projectRepository));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _executionRepository = executionRepository ?? throw new ArgumentNullException(nameof(executionRepository));
        _channelCatalog = channelCatalog ?? throw new ArgumentNullException(nameof(channelCatalog));
        _artifactBlobStore = artifactBlobStore ?? throw new ArgumentNullException(nameof(artifactBlobStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatchStore = dispatchStore;
        _dataClassificationGate = dataClassificationGate;
        _secretScanner = secretScanner;
    }

    public async Task<WorkflowReviewRequestResult> RequestAssignedReviewAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var guardedRunId = ApplicationGuard.NotBlank(runId, nameof(runId));

        var run = await _runRepository.GetByIdAsync(guardedRunId, cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            return RefuseAll(guardedRunId, null, null, null, $"Workflow run '{guardedRunId}' does not exist.");
        }

        if (run.IsTerminal)
        {
            return RefuseAll(
                guardedRunId,
                run.CurrentStageId,
                null,
                null,
                $"Workflow run '{guardedRunId}' is already terminal ({run.State}) and dispatches no reviewer.");
        }

        if (!run.IsTemplateBacked
            || run.TemplateId is not { } templateId
            || run.TemplateVersion is not { } templateVersion
            || string.IsNullOrWhiteSpace(run.TemplateSchemeSnapshotJson)
            || string.IsNullOrWhiteSpace(run.TemplateGraphSnapshotJson))
        {
            return RefuseAll(
                guardedRunId,
                run.CurrentStageId,
                null,
                null,
                $"Workflow run '{guardedRunId}' is not pinned to a template version, so it declares no "
                    + "assigned reviewer role or route. Roles and routes are read only from the pinned "
                    + "template of the run itself.");
        }

        // The stage, the roles and the required kind all come out of the run's own pinned scheme, never
        // out of the mutable template and never out of a display name.
        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, guardedRunId);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            return RefuseAll(
                guardedRunId,
                run.CurrentStageId,
                null,
                null,
                $"The pinned scheme of workflow run '{guardedRunId}' is unreadable, so no reviewer assignment "
                    + $"can be resolved: {exception.Message}");
        }

        var stage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (stage is null)
        {
            return RefuseAll(
                guardedRunId,
                run.CurrentStageId,
                null,
                null,
                $"Stage '{run.CurrentStageId}' is absent from the pinned scheme of workflow run "
                    + $"'{guardedRunId}', so no reviewer assignment exists for it.");
        }

        if (stage.RequiredReviewerRoles.Count == 0)
        {
            return RefuseAll(
                guardedRunId,
                stage.StageId,
                stage.ArtifactRequirement,
                null,
                $"Stage '{stage.StageId}' of workflow run '{guardedRunId}' declares no required reviewer roles "
                    + "in its pinned scheme, so there is no assigned review to request.");
        }

        if (stage.ArtifactRequirement is not { } requiredKind)
        {
            return RefuseAll(
                guardedRunId,
                stage.StageId,
                null,
                null,
                $"Stage '{stage.StageId}' of workflow run '{guardedRunId}' requires reviewer roles but "
                    + "declares no artifact kind, so there is no stored content a review could be about.");
        }

        // The content is the run's newest stored artifact of the required kind, and only its bytes decide
        // whether it can be reviewed at all.
        var artifact = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, guardedRunId, stage.StageId, requiredKind);

        if (artifact is null)
        {
            return RefuseAll(
                guardedRunId,
                stage.StageId,
                requiredKind,
                null,
                $"No stored '{requiredKind}' artifact is recorded for stage '{stage.StageId}' of workflow run "
                    + $"'{guardedRunId}', so there is nothing to send for review.");
        }

        var roles = stage.RequiredReviewerRoles.ToArray();
        var refusals = new List<WorkflowReviewRequestRoleOutcome>(roles.Length);
        var refusedOnly = true;

        foreach (var role in roles)
        {
            var outcome = await RequestForRoleAsync(
                    run,
                    stage,
                    requiredKind,
                    artifact,
                    role,
                    templateId,
                    templateVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            refusals.Add(outcome);

            if (outcome.Refusal is null)
            {
                refusedOnly = false;
            }
        }

        var result = new WorkflowReviewRequestResult(
            refusedOnly ? WorkflowReviewRequestOutcome.Refused : WorkflowReviewRequestOutcome.Dispatched,
            guardedRunId,
            stage.StageId,
            requiredKind,
            artifact.HashSha256,
            refusals,
            Describe(guardedRunId, stage.StageId, requiredKind, artifact, refusals, refusedOnly));

        return result;
    }

    /// <summary>
    /// One required role, from its pinned binding to whatever the dispatch actually produced.
    /// <para>
    /// Every refusal here happens before a row is written. Only after the route has been proven to be a real
    /// <c>Routes</c> row with a resolvable identity, and a channel has actually been resolved for it, is a
    /// session and an execution persisted - which is what keeps a refused request from leaving a pending
    /// reviewer turn that never existed.
    /// </para>
    /// </summary>
    private async Task<WorkflowReviewRequestRoleOutcome> RequestForRoleAsync(
        WorkflowRun run,
        WorkflowStageDefinition stage,
        string requiredKind,
        WorkflowArtifactEvidence artifact,
        string role,
        string templateId,
        int templateVersion,
        CancellationToken cancellationToken)
    {
        var runId = run.Id;

        var template = await _templateStore
            .GetAsync(templateId, templateVersion, cancellationToken)
            .ConfigureAwait(false);

        if (template is null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                assignedRouteId: null,
                assignedModelId: null,
                $"Template '{templateId}' version {templateVersion} pinned by workflow run '{runId}' is no "
                    + "longer readable, so the route assigned to role '" + role + "' cannot be resolved.");
        }

        var binding = template.RoleBindings
            .FirstOrDefault(candidate => string.Equals(candidate.RoleId, role, StringComparison.Ordinal));

        if (binding is null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                assignedRouteId: null,
                assignedModelId: null,
                $"Template '{templateId}' version {templateVersion} binds no route to the required reviewer "
                    + $"role '{role}' of stage '{stage.StageId}', so there is no assigned model to request.");
        }

        string routeId;

        try
        {
            routeId = binding.ResolveRoute();
        }
        catch (InvalidOperationException exception)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                assignedRouteId: null,
                assignedModelId: binding.ModelId,
                exception.Message);
        }

        // The assigned route has to be a row, and a row has to have an account, a profile and a model
        // behind it. A label such as 'route-opencode' is not a row and is refused here; it is never
        // inserted to make the refusal go away.
        //
        // A store that cannot read the row it was asked for - a backend name this build does not know, a
        // classification it does not recognise - refuses there rather than guessing. That refusal is the
        // answer too, and it names the route, so nothing is written and nothing is dispatched.
        WorkflowRouteAssignment? assignment;

        try
        {
            assignment = await _routeRepository
                .GetAssignmentAsync(routeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Route '{routeId}' assigned to reviewer role '{role}' of workflow run '{runId}' cannot be read, "
                    + $"so no model is attached to it and nothing is dispatched: {exception.Message}");
        }

        if (assignment is null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Route '{routeId}' assigned to reviewer role '{role}' of workflow run '{runId}' is not a "
                    + "persisted route, so no model is attached to it and no execution can be recorded "
                    + "against it.");
        }

        if (!assignment.HasEveryIdentity)
        {
            var missing = string.Join(", ", assignment.MissingIdentities);

            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Route '{routeId}' assigned to reviewer role '{role}' of workflow run '{runId}' is missing "
                    + $"the required {missing} identity, so the assigned model cannot be identified.");
        }

        if (!assignment.Route.IsEnabled)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Route '{routeId}' assigned to reviewer role '{role}' of workflow run '{runId}' is disabled, "
                    + "so no reviewer is dispatched to it.");
        }

        var capabilities = new[] { ReviewerCapability };
        IWorkflowNodeChannel channel;

        try
        {
            channel = _channelCatalog.ResolveChannel(routeId, capabilities);
        }
        catch (WorkflowValidationException exception)
        {
            // No production channel reports an independently observed route yet, so this is where the
            // shipped product stops. Nothing has been written at this point, and nothing is written after it
            // for this role: there is no pending reviewer turn to observe and nothing a later read could
            // mistake for a review in progress.
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                exception.Message);
        }

        // Re-read the run before anything is written. A stage that moved, a run that was replaced or an
        // artifact that was written since the observation above must not be reviewed under the identity that
        // was captured before those awaits.
        if (!await ConfirmsTargetAsync(run, stage.StageId, requiredKind, artifact.HashSha256, cancellationToken)
                .ConfigureAwait(false))
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"The stored target of reviewer role '{role}' of workflow run '{runId}' changed while the "
                    + "assignment was being resolved, so nothing was dispatched for it.");
        }

        var now = _timeProvider.GetUtcNow();
        var verifiedContent = await ReadVerifiedArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);

        if (verifiedContent is null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"The committed bytes of artifact '{artifact.ArtifactId}' of workflow run '{runId}' cannot be "
                    + "re-hashed and opened under the reviewer bound, so no reviewer is shown them.");
        }

        string prompt;

        try
        {
            if (_secretScanner is null)
                return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                    "A secret scanner is required before reviewer admission; nothing was dispatched.");

            // Scan the same bounded, hash-verified bytes used below, before writing any admission rows.
            // Neither finding snippets nor scanner exception messages belong in the operator refusal.
            WorkflowSecretScanReport scan;
            try
            {
                scan = await _secretScanner.ScanFilesAsync(new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["review/artifact.txt"] = verifiedContent.ToArray()
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                    "The verified review artifact could not be safely scanned; nothing was dispatched.");
            }
            if (scan is null || scan.HasFindings)
                return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                    "The verified review artifact did not pass the secret scan; nothing was dispatched.");

            prompt = BuildReviewPrompt(run, stage, artifact, role, verifiedContent);
        }
        finally
        {
            await verifiedContent.DisposeAsync().ConfigureAwait(false);
        }

        var project = await _projectRepository.GetByIdAsync(run.ProjectId, cancellationToken).ConfigureAwait(false);

        if (project is null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Project '{run.ProjectId}' of workflow run '{runId}' is no longer stored, so no run-scoped "
                    + "reviewer session can be recorded for it.");
        }

        if (_dataClassificationGate is null || !Enum.IsDefined(project.DataClassification)
            || !Enum.IsDefined(artifact.Classification) || !Enum.IsDefined(assignment.Route.MaxDataClass))
            return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                "Verified data classification and its safety service are required before reviewer dispatch.");

        var dataClass = (DataClassification)Math.Max((int)project.DataClassification, (int)artifact.Classification);
        var dataDecision = await _dataClassificationGate.EvaluateAsync(dataClass,
            assignment.Route.Binding.ProviderProfileId, isManualOnly: false, cancellationToken).ConfigureAwait(false);
        if (!dataDecision.IsAllowed || dataClass == DataClassification.Restricted || dataClass > assignment.Route.MaxDataClass)
            return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                dataDecision.Explanation ?? "Reviewer dispatch exceeds the route data policy or requires a verified fragment preview.");

        // This early read gives a useful refusal. It is not the concurrency authority: production
        // admission re-checks the unique role/artifact key inside the three-row transaction below.
        var attempts = (await _evidenceRepository
                .ListByRunIdAsync(runId, cancellationToken)
                .ConfigureAwait(false))
            .Where(candidate =>
                string.Equals(candidate.StageId, stage.StageId, StringComparison.Ordinal)
                && string.Equals(candidate.ReviewerRole, role, StringComparison.Ordinal)
                && string.Equals(candidate.ReviewedArtifactHash, artifact.HashSha256, StringComparison.Ordinal)
                && candidate.IsReadOnly).ToArray();
        var existing = attempts.FirstOrDefault(candidate =>
            candidate.ExecutionState is not (ExecutionState.Failed or ExecutionState.Cancelled));

        if (existing is not null)
        {
            return WorkflowReviewRequestRoleOutcome.RefusedRole(
                role,
                routeId,
                binding.ModelId,
                $"Reviewer role '{role}' of stage '{stage.StageId}' of workflow run '{runId}' already reviewed "
                    + $"artifact hash '{artifact.HashSha256}' in execution '{existing.ExecutionId}', so this is a "
                    + "duplicate request and nothing is dispatched. A new request is allowed "
                    + "only after a known Failed/Cancelled outcome or after the artifact is replaced.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var executionId = Guid.NewGuid().ToString("N");

        var session = new Session(
            sessionId,
            assignment.Route.Binding,
            run.ProjectId,
            project.RootPath,
            nativeSessionId: null,
            SessionState.Starting,
            ReconciliationOutcome.None,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: runId,
            role,
            activeExecutionId: executionId,
            createdAt: now,
            lastEventAt: now);

        var execution = new Execution(
            executionId,
            sessionId,
            clientRequestId: executionId,
            ExecutionState.Queued,
            ExecutionFailureReason.None,
            assignment.Route.Id,
            observedRouteId: null,
            retryOfExecutionId: attempts.LastOrDefault()?.ExecutionId,
            processState: null,
            exitCode: null,
            terminationReason: null,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            createdAt: now,
            startedAt: null,
            endedAt: null);

        var evidence = new ReviewerExecutionEvidence(
            executionId,
            sessionId,
            runId,
            role,
            stage.StageId,
            assignment.Route.Id,
            observedRouteId: null,
            artifact.ArtifactId,
            artifact.HashSha256,
            isReadOnly: true,
            ExecutionState.Queued);

        // The requested route and the reviewed artifact are durable before the turn exists at all, so a
        // crash between here and the backend's answer leaves a row that authorizes nothing rather than no
        // row at all.
        if (!await TryAdmitAsync(session, execution, evidence, prompt, cancellationToken).ConfigureAwait(false))
            return WorkflowReviewRequestRoleOutcome.RefusedRole(role, routeId, binding.ModelId,
                "The review target changed or another request already admitted this role and artifact; no additional turn was dispatched.");

        var turn = await DispatchAsync(
                channel,
                run,
                stage,
                binding,
                executionId,
                routeId,
                prompt,
                cancellationToken)
            .ConfigureAwait(false);

        var state = turn.State;
        var observedRouteId = turn.ObservedRouteId;
        var observed = evidence.WithObservedOutcome(observedRouteId, state);

        // Dispatch has already happened. Caller cancellation must not erase its observed terminal
        // outcome; use a separate bounded commit deadline, propagate storage failure, and never retry.
        using var persistenceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = Complete(execution, state, observedRouteId, now);
        var uncertain = state is not (ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled);
        var completedSession = new Session(session.Id, session.Binding, session.ProjectId, session.WorkspaceRootPath,
            SafeNativeSessionId(turn.NativeSessionId), uncertain ? SessionState.Ambiguous : SessionState.Closed,
            uncertain ? ReconciliationOutcome.Ambiguous : ReconciliationOutcome.None, CloseReason.None,
            session.ContinuationOfSessionId, session.ForkedFromSessionId, session.WorkflowRunId, session.Role,
            uncertain ? execution.Id : null, session.CreatedAt, _timeProvider.GetUtcNow());
        if (_dispatchStore is not null)
            await _dispatchStore.CompleteWithResponseAsync(completedSession, completed, observed, turn.Response, persistenceDeadline.Token).ConfigureAwait(false);
        else
        {
            if (turn.Response is not null)
                throw new NotSupportedException("An atomic dispatch store is required to retain reviewer response evidence.");
            await _executionRepository.UpsertAsync(completed, persistenceDeadline.Token).ConfigureAwait(false);
            await _evidenceRepository.UpdateObservedAsync(observed, persistenceDeadline.Token).ConfigureAwait(false);
            await _sessionRepository.UpsertAsync(completedSession, persistenceDeadline.Token).ConfigureAwait(false);
        }

        return new WorkflowReviewRequestRoleOutcome(
            role,
            assignment.Route.Id,
            binding.ModelId,
            refusal: null,
            sessionId,
            executionId,
            state,
            observedRouteId,
            readOnly: true,
            boundToArtifact: true);
    }

    private static string? SafeNativeSessionId(string? value) => value is { Length: > 0 and <= 512 }
        && !value.Any(char.IsControl)
        && new LLMWorkGUI.Application.Security.CredentialTextRedactor().Redact(value) == value ? value : null;

    private async Task<bool> TryAdmitAsync(Session session, Execution execution,
        ReviewerExecutionEvidence evidence, string prompt, CancellationToken cancellationToken)
    {
        if (_dispatchStore is not null)
            return await _dispatchStore.TryAdmitWithPromptAsync(session, execution, evidence, prompt, cancellationToken).ConfigureAwait(false);

        // Compatibility for lightweight repository-only callers. Production always injects the atomic
        // store; this serialized fallback makes no claim of rollback across independent repositories.
        await _fallbackAdmissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _evidenceRepository.ListByRunIdAsync(evidence.WorkflowRunId, cancellationToken)
                .ConfigureAwait(false);
            if (existing.Any(candidate => candidate.StageId == evidence.StageId &&
                candidate.ReviewerRole == evidence.ReviewerRole && candidate.ReviewedArtifactHash == evidence.ReviewedArtifactHash &&
                candidate.IsReadOnly == evidence.IsReadOnly &&
                candidate.ExecutionState is not (ExecutionState.Failed or ExecutionState.Cancelled))) return false;
            await _sessionRepository.UpsertAsync(session, cancellationToken).ConfigureAwait(false);
            await _executionRepository.UpsertAsync(execution, cancellationToken).ConfigureAwait(false);
            await _evidenceRepository.SaveAsync(evidence, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _fallbackAdmissionGate.Release(); }
    }

    /// <summary>
    /// The turn itself, and nothing more. The observed route is whatever the channel reported and is null
    /// whenever the channel reported nothing, failed, was cancelled or threw: a route that was requested is
    /// never the answer to the question of which route was used.
    /// <para>
    /// A thrown exception is recorded as <see cref="ExecutionState.Ambiguous"/> rather than as a failure,
    /// because this process cannot know whether the request reached the model before the fault. A cancelled
    /// token does not prove native termination; it remains ambiguous unless the channel reports a confirmed
    /// terminal outcome. Nothing here
    /// retries: a second attempt would be a second delivery of the same request with no record of whether
    /// the first one landed.
    /// </para>
    /// </summary>
    private async Task<WorkflowChannelTurnResult> DispatchAsync(
        IWorkflowNodeChannel channel,
        WorkflowRun run,
        WorkflowStageDefinition stage,
        RoleBindingDefinition binding,
        string executionId,
        string routeId,
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new WorkflowChannelTurnRequest(
            executionId,
            new WorkflowNodeDefinition(
                stage.StageId,
                WorkflowNodeKind.Review,
                stage.DisplayName,
                binding.RoleId,
                binding.RequiredCapabilities,
                binding.PrimaryRouteId,
                binding.FallbackRouteIds,
                gateMetadata: WorkflowNodeGateMetadata.CreateFrom(stage)),
            binding,
            routeId,
            isReadOnly: true,
            promptOrCommand: prompt);

        try
        {
            var turn = await channel.ExecuteTurnAsync(request, cancellationToken).ConfigureAwait(false);

            // A turn that reported no route, or a different one, is a mismatch - never a success with the
            // requested route filled in for the missing observation.
            var state = turn.State == ExecutionState.Succeeded
                     && !string.Equals(turn.ObservedRouteId, routeId, StringComparison.Ordinal)
                ? ExecutionState.RouteMismatch
                : turn.State;

            return new(state, turn.ObservedRouteId, turn.NativeSessionId, response: turn.Response);
        }
        catch (OperationCanceledException)
        {
            return new(ExecutionState.Ambiguous);
        }
        catch (WorkflowValidationException)
        {
            // A validation exception from inside ExecuteTurnAsync does not prove non-delivery.
            return new(ExecutionState.Ambiguous);
        }
        catch (Exception)
        {
            return new(ExecutionState.Ambiguous);
        }
    }

    /// <summary>
    /// The execution as it stands after the turn, carrying the observed route exactly as it was reported.
    /// Everything else about the execution is preserved: only the state, the failure class, the observation
    /// and the end stamp are decided here, so a turn that came back mismatched keeps the route it really
    /// used rather than being rewritten to the route that was asked for.
    /// </summary>
    private Execution Complete(
        Execution execution,
        ExecutionState state,
        string? observedRouteId,
        DateTimeOffset startedAt) =>
        new(
            execution.Id,
            execution.SessionId,
            execution.ClientRequestId,
            state,
            state == ExecutionState.Succeeded ? ExecutionFailureReason.None : ExecutionFailureReason.InternalError,
            execution.RequestedRouteId,
            observedRouteId,
            execution.RetryOfExecutionId,
            execution.ProcessState,
            execution.ExitCode,
            execution.TerminationReason,
            execution.Artifacts,
            execution.SourceHashBefore,
            execution.SourceHashAfter,
            execution.CreatedAt,
            startedAt,
            _timeProvider.GetUtcNow());

    /// <summary>
    /// The verified bytes of the artifact, read through the store's verify-then-read contract under a
    /// bound, or null when they cannot be established or are not faithful text.
    /// <para>
    /// The store verifies a file and then opens a second, independent handle on it, so the handle this
    /// method is handed is not provably the handle that was verified: the file can be replaced, truncated or
    /// relinked between the two steps, and a content-addressed store is exactly the place where that
    /// happens. The bytes are therefore hashed again here, after the copy and before anything is built from
    /// them, and compared with the hash the artifact row was recorded with. That comparison is the only
    /// statement this system can make that the reviewer is shown the artifact the gate is about, and a
    /// stream that yields different bytes is refused rather than forwarded.
    /// </para>
    /// <para>
    /// The bound exists because a content hash proves authenticity and never a size, and a reviewer turn
    /// that cannot be shown the whole document is refused by name instead of being shown a prefix. The
    /// decoding is strict for the same reason: a rendering built from replacement characters is not the
    /// artifact, and a model asked to approve bytes it did not really receive would be approving nothing.
    /// </para>
    /// </summary>
    private async Task<MemoryStream?> ReadVerifiedArtifactAsync(
        WorkflowArtifactEvidence artifact,
        CancellationToken cancellationToken)
    {
        Stream? content;

        try
        {
            content = await _artifactBlobStore
                .OpenVerifiedAsync(artifact.BlobId, MaxReviewArtifactBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowArtifactTooLargeException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (content is null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();

            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (buffer.Length > MaxReviewArtifactBytes)
            {
                return null;
            }

            var bytes = buffer.ToArray();

            if (!IsTheRecordedArtifact(artifact, bytes))
            {
                return null;
            }

            if (!IsFaithfulUtf8(bytes))
            {
                return null;
            }

            return new MemoryStream(bytes, writable: false);
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            await content.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the bytes this method actually copied are the bytes the artifact row was recorded with.
    /// <para>
    /// The comparison is exact and is against the recorded hash, which is the same string as the blob id the
    /// store was just asked for - so checking either is checking the same claim, and hashing the copied bytes
    /// is the only one of the three that can fail. Both sides are put in the canonical
    /// <c>sha256:&lt;lowercase hex&gt;</c> form first and compared as strings, never as normalised values:
    /// a row whose recorded hash is not in that form is not hashed as anything else, it simply fails.
    /// </para>
    /// </summary>
    private static bool IsTheRecordedArtifact(WorkflowArtifactEvidence artifact, byte[] bytes) =>
        string.Equals(
            ContentHashOf(bytes),
            artifact.HashSha256,
            StringComparison.Ordinal);

    /// <summary>
    /// The canonical address of a byte sequence, in exactly the form
    /// <see cref="WorkflowArtifactEvidence.ContentHashPrefix"/> declares.
    /// </summary>
    private static string ContentHashOf(byte[] bytes) =>
        WorkflowArtifactEvidence.ContentHashPrefix
        + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Whether the verified bytes are exactly what a text turn can be shown. A byte-order mark and a
    /// trailing newline are accepted and left alone; a replacement character is not, because its presence
    /// means the encoding did not round-trip and the reviewer would be shown something other than the
    /// artifact.
    /// </summary>
    private static bool IsFaithfulUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

            // Strict decoding already rejects malformed byte sequences. An explicitly encoded
            // U+FFFD is valid artifact content, not evidence of lossy decoder substitution.
            _ = strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The prompt the reviewer channel receives: the identity of what it is being asked about, the exact
    /// verified bytes, and the contract its answer has to meet. No verdict is parsed here - this slice stops
    /// at the dispatch boundary on purpose.
    /// </summary>
    private static string BuildReviewPrompt(
        WorkflowRun run,
        WorkflowStageDefinition stage,
        WorkflowArtifactEvidence artifact,
        string role,
        MemoryStream verifiedContent)
    {
        verifiedContent.Position = 0;

        using var reader = new StreamReader(verifiedContent, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        var responseContract = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = WorkflowReviewResponseParser.SchemaVersion, runId = run.Id, stageId = stage.StageId,
            reviewerRole = role, artifactId = artifact.ArtifactId, artifactSha256 = artifact.HashSha256,
            verdict = "RequestChanges", summary = "Describe the findings in at most 4096 characters."
        });
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Read-only review request. You are the assigned reviewer for role '{role}' of stage '{stage.StageId}' of workflow run '{run.Id}'.\nArtifact id: {artifact.ArtifactId}\nArtifact kind: {artifact.Kind}\nSHA-256 of the reviewed bytes: {artifact.HashSha256}\n--- begin verified artifact ---\n{text}\n--- end verified artifact ---\nReport only what this artifact states. Do not modify anything: this turn is read-only.\nReturn exactly one JSON object without Markdown fences, extra properties or surrounding prose, using this schema and these target identifiers: {responseContract}\nChoose verdict Approve, Reject, or RequestChanges based on your review. All other identity fields must match exactly.");
    }

    /// <summary>
    /// Whether the run, its stage, its pinned requirement and its current artifact are still the ones the
    /// request was resolved against, re-read from rows after the assignment awaits.
    /// </summary>
    private async Task<bool> ConfirmsTargetAsync(
        WorkflowRun run,
        string stageId,
        string kind,
        string hash,
        CancellationToken cancellationToken)
    {
        var stored = await _runRepository.GetByIdAsync(run.Id, cancellationToken).ConfigureAwait(false);

        if (stored is null
            || stored.IsTerminal
            || !string.Equals(stored.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(stored.Artifacts, run.Id, stageId, kind);

        return current is not null && string.Equals(current.HashSha256, hash, StringComparison.Ordinal);
    }

    private static string Describe(
        string runId,
        string stageId,
        string kind,
        WorkflowArtifactEvidence artifact,
        IReadOnlyList<WorkflowReviewRequestRoleOutcome> roles,
        bool refusedOnly)
    {
        if (roles.Count == 0)
        {
            return $"Run '{runId}' declares no reviewer role at stage '{stageId}'.";
        }

        var dispatched = roles.Where(role => role.Refusal is null).ToArray();
        var observed = dispatched.Count(role => role.ObservedAssignedRoute);

        if (refusedOnly)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{runId}' stage '{stageId}' ('{kind}', {artifact.HashSha256}): {roles.Count} of {roles.Count} assigned reviewer role(s) refused; nothing was dispatched and nothing was recorded.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Run '{runId}' stage '{stageId}' ('{kind}', {artifact.HashSha256}): {dispatched.Length} of {roles.Count} assigned reviewer role(s) dispatched, {observed} of them reported the assigned route back. No verdict is recorded by this action.");
    }

    private static WorkflowReviewRequestResult RefuseAll(
        string runId,
        string? stageId,
        string? kind,
        string? hash,
        string detail) =>
        new(
            WorkflowReviewRequestOutcome.Refused,
            runId,
            stageId,
            kind,
            hash,
            Array.Empty<WorkflowReviewRequestRoleOutcome>(),
            detail);
}
