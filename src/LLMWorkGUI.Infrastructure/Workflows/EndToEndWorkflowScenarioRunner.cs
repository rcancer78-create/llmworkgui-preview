using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Default end-to-end hardening acceptance scenario. It proves every fail-closed pre-coder gate
/// blocker on its own, re-runs the gate after a rework, proves that the AGY profile and the Codex
/// <c>CODEX_HOME</c> switch are refused with the named missing native-session proof while the Mirasim
/// host stays untouched, and recovers a failed run to completion. All collaborators are deterministic
/// services; no live model call is made and no native account-context switch is claimed (ROADMAP Phase
/// 12 exit criteria).
/// </summary>
public sealed class EndToEndWorkflowScenarioRunner : IEndToEndWorkflowScenarioRunner
{
    public const string PrimaryRouteId = "route-opencode";
    public const string AgyRouteId = "route-star-cliproxy-agy";
    public const string CodexRouteId = "route-star-cliproxy-codex";
    public const string DocumentHash = "sha256:" + "d0c0ffee" + "00000000000000000000000000000000000000000000000000000000";

    private const string WorkflowArtifactEvidencePrefix = "sha256:";

    private const string RecoveryProjectId = "project-e2e-recovery";
    private const string RecoveryPackageId = "pkg-e2e-recovery";
    private const string RecoveryVersionId = "ver-e2e-recovery";
    private const int MaxRecoveryTransitions = 32;

    private readonly IPreCoderGateValidator? _gateValidator;
    private readonly IWorkflowStudioService? _studioService;
    private readonly IWorkflowRunService? _runService;
    private readonly IWorkflowArtifactBlobStore? _artifactBlobStore;
    private readonly WorkflowScheme? _scheme;
    private readonly TimeProvider _timeProvider;

    public EndToEndWorkflowScenarioRunner(
        IPreCoderGateValidator? gateValidator = null,
        IWorkflowStudioService? studioService = null,
        IWorkflowRunService? runService = null,
        WorkflowScheme? scheme = null,
        TimeProvider? timeProvider = null,
        IWorkflowArtifactBlobStore? artifactBlobStore = null)
    {
        _gateValidator = gateValidator;
        _studioService = studioService;
        _runService = runService;
        _artifactBlobStore = artifactBlobStore;
        _scheme = scheme;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EndToEndWorkflowScenarioReport> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var blockers = new List<EndToEndScenarioBlockerEvidence>();
        var warnings = new List<string>();

        var reworkAllowed = false;
        var agyProfileSwitchRefused = false;
        var codexHomeSwitchRefused = false;
        var refusalNamed = false;
        var mirasimUntouched = false;
        var runFailureRecovered = false;

        if (_gateValidator is null || _studioService is null || _runService is null || _scheme is null)
        {
            warnings.Add(
                "The end-to-end scenario requires the pre-coder gate, the workflow studio, the run "
                + "service and the workflow scheme; at least one of them is missing from the composition.");
        }
        else if (_artifactBlobStore is null)
        {
            warnings.Add(
                "The end-to-end scenario recovers a run on the standard scheme, whose stages are gated by a "
                + "stored artifact; the artifact blob store is missing from the composition.");
        }
        else
        {
            try
            {
                EvaluateGateBlockers(blockers, cancellationToken);
                reworkAllowed = EvaluateRework(blockers, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add("The pre-coder gate blocker proofs failed: " + exception.Message);
            }

            try
            {
                (agyProfileSwitchRefused, codexHomeSwitchRefused, refusalNamed, mirasimUntouched) =
                    await VerifyAccountContextRefusalAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add("The account-context refusal proof failed: " + exception.Message);
            }

            try
            {
                runFailureRecovered = await VerifyRunFailureRecoveryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add("The run failure/recovery proof failed: " + exception.Message);
            }
        }

        return new EndToEndWorkflowScenarioReport
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            BlockerEvidences = blockers,
            ReworkApprovalAllowed = reworkAllowed,
            AgyProfileSwitchRefused = agyProfileSwitchRefused,
            CodexHomeSwitchRefused = codexHomeSwitchRefused,
            NativeSwitchProofRefusalNamed = refusalNamed,
            MirasimHostUntouched = mirasimUntouched,
            RunFailureRecovered = runFailureRecovered,
            Warnings = warnings
        };
    }

    private void EvaluateGateBlockers(
        List<EndToEndScenarioBlockerEvidence> blockers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var missingReviewer = _gateValidator!.Evaluate(CreateGateRequest(
            new[]
            {
                CreateGateDocument(
                    CreateVerdicts(("Reviewer", WorkflowReviewVerdict.Approve), ("Architect", WorkflowReviewVerdict.Approve)),
                    CreateApproval())
            }));

        blockers.Add(new EndToEndScenarioBlockerEvidence(
            EndToEndScenarioBlockerIds.MissingReviewer,
            "Обязательный ревьюер отсутствует",
            WorkflowScheme.CodeAndUiStageId,
            missingReviewer.HasMissingReviewer
                && !missingReviewer.HasConflictingVerdicts
                && !missingReviewer.IsHashMismatch
                && !missingReviewer.HasMissingUiArtifact,
            missingReviewer.Summary));

        var conflicting = _gateValidator.Evaluate(CreateGateRequest(
            new[]
            {
                CreateGateDocument(
                    CreateVerdicts(
                        ("Reviewer", WorkflowReviewVerdict.Approve),
                        ("Reviewer", WorkflowReviewVerdict.Reject),
                        ("Architect", WorkflowReviewVerdict.Approve),
                        ("UiReviewer", WorkflowReviewVerdict.Approve)),
                    CreateApproval())
            }));

        blockers.Add(new EndToEndScenarioBlockerEvidence(
            EndToEndScenarioBlockerIds.ConflictingVerdicts,
            "Конфликт вердиктов approve + reject",
            WorkflowScheme.CodeAndUiStageId,
            conflicting.HasConflictingVerdicts
                && !conflicting.IsHashMismatch
                && !conflicting.HasMissingUiArtifact,
            conflicting.Summary));

        var tamperedHash = "sha256:" + "beefbeef" + "11111111111111111111111111111111111111111111111111111111";

        var hashMismatch = _gateValidator.Evaluate(CreateGateRequest(
            new[]
            {
                CreateGateDocument(
                    CreateVerdicts(
                        ("Reviewer", WorkflowReviewVerdict.Approve),
                        ("Architect", WorkflowReviewVerdict.Approve),
                        ("UiReviewer", WorkflowReviewVerdict.Approve)),
                    CreateApproval(),
                    contentHash: tamperedHash)
            }));

        blockers.Add(new EndToEndScenarioBlockerEvidence(
            EndToEndScenarioBlockerIds.HashMismatch,
            "Изменение хэша документа после ревью",
            WorkflowScheme.CodeAndUiStageId,
            hashMismatch.IsHashMismatch,
            hashMismatch.Summary));

        var missingUiArtifact = _gateValidator.Evaluate(CreateGateRequest(
            new[]
            {
                CreateGateDocument(
                    CreateVerdicts(
                        ("Reviewer", WorkflowReviewVerdict.Approve),
                        ("Architect", WorkflowReviewVerdict.Approve),
                        ("UiReviewer", WorkflowReviewVerdict.Approve)),
                    CreateApproval(),
                    hasUiArtifact: false)
            }));

        blockers.Add(new EndToEndScenarioBlockerEvidence(
            EndToEndScenarioBlockerIds.MissingUiArtifact,
            "Обязательное UI-доказательство отсутствует",
            WorkflowScheme.CodeAndUiStageId,
            missingUiArtifact.HasMissingUiArtifact
                && !missingUiArtifact.HasMissingReviewer
                && !missingUiArtifact.HasConflictingVerdicts
                && !missingUiArtifact.IsHashMismatch,
            missingUiArtifact.Summary));
    }

    private bool EvaluateRework(
        List<EndToEndScenarioBlockerEvidence> blockers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var verdicts = CreateVerdicts(
            ("Reviewer", WorkflowReviewVerdict.Approve),
            ("Architect", WorkflowReviewVerdict.Approve),
            ("UiReviewer", WorkflowReviewVerdict.Approve));

        var approval = CreateApproval();
        var documents = WorkflowStudioDocumentRules.RequiredDocumentKinds
            .Select(kind => new PreCoderGateDocument(
                kind,
                "draft-" + kind,
                DocumentHash,
                Version: 2,
                verdicts,
                approval,
                HasUiArtifact: true,
                HasVisualAcceptance: true))
            .ToArray();

        var result = _gateValidator!.Evaluate(new PreCoderGateRequest(
            WorkflowStudioDocumentRules.RequiredDocumentKinds,
            WorkflowStudioDocumentRules.RequiredReviewerRoles,
            documents,
            WorkflowScheme.CodeAndUiStageId,
            PrimaryRouteId,
            PrimaryRouteId,
            requiresUiArtifact: true,
            requiresUserVisualAcceptance: true));

        return result.IsAllowed;
    }

    /// <summary>
    /// Proves the account-context boundary is honest: a well-formed AGY and Codex switch request is
    /// refused with the named missing proof, no native session or observed route appears, and the Mirasim
    /// snapshot the caller handed in comes back untouched. A green result here is a proven refusal, never
    /// a proven switch.
    /// </summary>
    private async Task<(bool Agy, bool Codex, bool RefusalNamed, bool MirasimUntouched)>
        VerifyAccountContextRefusalAsync(CancellationToken cancellationToken)
    {
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true);

        cancellationToken.ThrowIfCancellationRequested();

        var agy = await _studioService!.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-agy-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: AgyRouteId,
                MirasimState: mirasim),
            cancellationToken).ConfigureAwait(false);

        var codexHome = Path.Combine(Path.GetTempPath(), "llmworkgui-codex-home-scenario");

        var codex = await _studioService.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account-alpha",
                PreviousNativeSessionId: "native-codex-old",
                CodexHomePath: codexHome,
                RequestedRouteId: CodexRouteId,
                MirasimState: mirasim),
            cancellationToken).ConfigureAwait(false);

        var agyRefused = IsRefusedWithoutNativeSession(agy);
        var codexRefused = IsRefusedWithoutNativeSession(codex);

        // The refusal has to name what is missing, otherwise a screen can read it as an unexplained
        // failure. The check is on the shape of the sentence, not on one product's exact wording.
        var refusalNamed = agyRefused
            && codexRefused
            && NamesMissingProof(agy.FailureReason)
            && NamesMissingProof(codex.FailureReason);

        var mirasimUntouched = ReferenceEquals(agy.MirasimState, mirasim)
            && ReferenceEquals(codex.MirasimState, mirasim)
            && string.Equals(mirasim.ActiveAccountId, "mirasim-account-1", StringComparison.Ordinal)
            && string.Equals(mirasim.RelayState, "relay-active", StringComparison.Ordinal)
            && mirasim.RecordingEnabled;

        return (agyRefused, codexRefused, refusalNamed, mirasimUntouched);
    }

    /// <summary>
    /// A refused switch: not applied, no native session, no observed route, and nothing carried over from
    /// the previous session or its credentials.
    /// </summary>
    private static bool IsRefusedWithoutNativeSession(WorkflowAccountContextSwitchResult result) =>
        !result.IsSwitched
        && result.IsBlocked
        && result.Refusal == WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof
        && result.NativeSessionId is null
        && result.ObservedRouteId is null
        && result.RequiresNewNativeSession
        && !result.CarriesPreviousSession
        && !result.CredentialsTransferred;

    private static bool NamesMissingProof(string? failureReason) =>
        failureReason is not null
        && failureReason.Contains("refused, not applied", StringComparison.Ordinal)
        && failureReason.Contains("executable", StringComparison.Ordinal)
        && failureReason.Contains("unique route key", StringComparison.Ordinal)
        && failureReason.Contains("no native session is created", StringComparison.Ordinal);

    private async Task<bool> VerifyRunFailureRecoveryAsync(CancellationToken cancellationToken)
    {
        // The deterministic hardening scenario predates template pinning and drives the standard scheme
        // directly, so it starts its runs through the explicit legacy path instead of requiring a template
        // assignment for a project that has none.
        var failed = await _runService!
            .StartLegacyRunAsync(
                RecoveryProjectId,
                RecoveryPackageId,
                RecoveryVersionId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        failed = await _runService
            .FailRunAsync(failed.Id, "simulated provider failure", cancellationToken)
            .ConfigureAwait(false);

        var recovered = await _runService
            .StartLegacyRunAsync(
                RecoveryProjectId,
                RecoveryPackageId,
                RecoveryVersionId,
                sessionId: "session-recovery",
                cancellationToken)
            .ConfigureAwait(false);

        recovered = await AdvanceToCompletionAsync(recovered, cancellationToken).ConfigureAwait(false);

        return failed.State == WorkflowRunState.Failed
            && failed.TerminalOutcome == WorkflowTerminalOutcome.Failed
            && failed.EndedAtUtc is not null
            && recovered.State == WorkflowRunState.Completed
            && recovered.TerminalOutcome == WorkflowTerminalOutcome.Completed;
    }

    private async Task<WorkflowRun> AdvanceToCompletionAsync(
        WorkflowRun run,
        CancellationToken cancellationToken)
    {
        var minute = 0;
        var transitions = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++transitions > MaxRecoveryTransitions)
            {
                throw new InvalidOperationException(
                    "The recovery run did not reach a terminal stage within the expected number of transitions.");
            }

            var stage = _scheme!.GetRequiredStage(run.CurrentStageId);

            if (stage.NextStageId is null)
            {
                return await _runService!
                    .CompleteRunAsync(run.Id, "recovered and completed", cancellationToken)
                    .ConfigureAwait(false);
            }

            // The standard scheme gates every stage by a stored artifact, so the recovery walk produces the
            // content it then records, in the order a real stage does: the artifact first, then the verdicts
            // and the approval pinned to its hash, then the transition. The content is deterministic, so the
            // hash of what was recorded is known before the store reports it back.
            var artifactContent = CreateArtifactContent(stage.StageId);
            var artifactHash = WorkflowArtifactEvidencePrefix
                + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(artifactContent))
                    .ToLowerInvariant();

            if (stage.ArtifactRequirement is { } artifactKind)
            {
                run = await _runService!
                    .RecordStageArtifactAsync(
                        run.Id,
                        stage.StageId,
                        artifactKind,
                        new MemoryStream(artifactContent, writable: false),
                        DataClassification.PrivateSource,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var role in stage.RequiredReviewerRoles)
            {
                // The recovery walk is a deterministic acceptance simulation over a legacy, non-pinned run:
                // it makes no model call and observes no backend, so its verdicts are written through the
                // explicitly legacy, unlinked path. That path is refused for a run pinned to a template
                // version, which is what keeps this simulation from ever standing in as model-review
                // evidence for a production stage.
                run = await _runService!
                    .RecordLegacyUnlinkedReviewerVerdictAsync(
                        run.Id,
                        new ReviewerVerdictRecord(
                            role,
                            PrimaryRouteId,
                            stage.ArtifactRequirement is null ? DocumentHash : artifactHash,
                            WorkflowReviewVerdict.Approve,
                            "recovery evidence",
                            _timeProvider.GetUtcNow().AddMinutes(minute++)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stage.RequiresUserApproval)
            {
                run = await _runService!
                    .RecordUserApprovalAsync(
                        run.Id,
                        new UserApprovalEvidence(
                            "approval-" + stage.StageId,
                            "user",
                            stage.StageId,
                            stage.ArtifactRequirement is null ? DocumentHash : artifactHash,
                            UserApprovalDecision.Approved,
                            "approved during recovery",
                            _timeProvider.GetUtcNow().AddMinutes(minute++)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            run = await _runService!
                .AdvanceStageAsync(run.Id, "recovered transition", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The content a recovery stage records. The bytes are a pure function of the stage, so the walk stays
    /// deterministic and the hash of the recorded artifact is reproducible without reading the blob back.
    /// </summary>
    private static byte[] CreateArtifactContent(string stageId) =>
        System.Text.Encoding.UTF8.GetBytes($"end-to-end recovery artifact for stage '{stageId}'");

    private static PreCoderGateRequest CreateGateRequest(IReadOnlyList<PreCoderGateDocument> documents) =>
        new(
            new[] { DocumentTemplateKind.ProblemStatement },
            WorkflowStudioDocumentRules.RequiredReviewerRoles,
            documents,
            WorkflowScheme.CodeAndUiStageId,
            PrimaryRouteId,
            PrimaryRouteId,
            requiresUiArtifact: true,
            requiresUserVisualAcceptance: false);

    private static PreCoderGateDocument CreateGateDocument(
        IReadOnlyList<ReviewerVerdictRecord> verdicts,
        UserApprovalEvidence approval,
        string? contentHash = null,
        bool hasUiArtifact = true) =>
        new(
            DocumentTemplateKind.ProblemStatement,
            "draft-problem-statement",
            contentHash ?? DocumentHash,
            1,
            verdicts,
            approval,
            hasUiArtifact,
            HasVisualAcceptance: true);

    private static IReadOnlyList<ReviewerVerdictRecord> CreateVerdicts(
        params (string Role, WorkflowReviewVerdict Verdict)[] verdicts) =>
        verdicts
            .Select((entry, index) => new ReviewerVerdictRecord(
                entry.Role,
                PrimaryRouteId,
                DocumentHash,
                entry.Verdict,
                "scenario evidence",
                DateTimeOffset.UnixEpoch.AddMinutes(index)))
            .ToArray();

    private static UserApprovalEvidence CreateApproval() =>
        new(
            "approval-problem-statement",
            "user",
            WorkflowScheme.UserApprovalStageId,
            DocumentHash,
            UserApprovalDecision.Approved,
            "approved",
            DateTimeOffset.UnixEpoch.AddMinutes(10));
}
