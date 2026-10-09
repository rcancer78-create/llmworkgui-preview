using System.Globalization;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Whether the observed run's current stage has a reviewer gate to report at all, and if not, which named
/// refusal or unknown state explains it. There is no "quietly empty" value here: a stage that requires no
/// reviewers and a run whose pinned scheme cannot be read are different facts and are reported differently.
/// </summary>
public enum WorkflowReviewGateAvailability
{
    /// <summary>No run is being observed, so there is no gate to describe.</summary>
    NoObservedRun,

    /// <summary>
    /// The observed run's pinned current stage declares no required reviewer roles. The gate is not open
    /// because it does not exist at this stage, which is not the same statement as "satisfied".
    /// </summary>
    NoReviewerGate,

    /// <summary>
    /// The roles, the stage or the artifact requirement could not be read out of the run's own pinned scheme
    /// snapshot: the run is not template-backed, its stored document is unreadable, or its current stage is
    /// absent from it. Nothing is substituted for what the run actually pinned.
    /// </summary>
    PinnedSchemeUnusable,

    /// <summary>
    /// The stage declares required reviewer roles but no artifact kind, so there is no stored content a
    /// verdict could be about. Reported as unknown rather than as a gate with missing verdicts.
    /// </summary>
    ArtifactRequirementUndeclared,

    /// <summary>No stored artifact of the required run, stage and kind exists for this run yet.</summary>
    ArtifactMissing,

    /// <summary>
    /// The newest stored artifact exists but its committed bytes are missing or no longer hash to the
    /// recorded digest, so no role is presented as satisfied. The panel fails closed on the newest row and
    /// never falls back to an older artifact that did verify.
    /// </summary>
    ArtifactUnverified,

    /// <summary>
    /// The gate was evaluated against a verified current artifact and every role carries its own state.
    /// Whether the gate is satisfied is then read off the role states, never assumed from this value.
    /// </summary>
    Evaluated
}

/// <summary>
/// What the persisted evidence says about one required reviewer role on the current verified artifact hash.
/// Only <see cref="Approved"/> counts towards a satisfied role: every other value is a state the operator
/// has to see, and none of them is a shortened form of approval.
/// </summary>
public enum WorkflowReviewGateRoleState
{
    /// <summary>The newest verdict for this role on the current hash is <c>Approve</c>.</summary>
    Approved,

    /// <summary>The newest verdict for this role on the current hash is <c>Reject</c>.</summary>
    Rejected,

    /// <summary>The newest verdict for this role on the current hash is <c>RequestChanges</c>.</summary>
    RequestChanges,

    /// <summary>
    /// The newest verdict for this role on the current hash is itself a <c>Missing</c> verdict: the reviewer
    /// recorded that it reported nothing, which is a stored fact and not the absence of a row.
    /// </summary>
    ReportedMissing,

    /// <summary>No verdict for this role exists on the current hash at all.</summary>
    Missing,

    /// <summary>
    /// No verdict for this role exists on the current hash, but the role does have verdicts pinned to other
    /// hashes. Those describe an artifact that is no longer the current one, so they authorize nothing here.
    /// </summary>
    StaleHash,

    /// <summary>
    /// The newest matching row for this role cannot be read as evidence - a blank role or route, a
    /// non-content hash - so the role fails closed instead of falling back to an older readable row.
    /// </summary>
    EvidenceUnusable,

    /// <summary>
    /// The role's recorded verdict is named below, but the artifact it is about could not be re-hashed, so
    /// the role is not presented as satisfied however recent that verdict is.
    /// </summary>
    ArtifactUnverified
}

/// <summary>
/// One required reviewer role of the observed run's current stage, described entirely from stored rows.
/// Nothing here is operator input, and nothing is derived from a Studio draft, a mutable template, a
/// displayed route label or typed text.
/// </summary>
public sealed record WorkflowReviewGateRoleStatus
{
    public WorkflowReviewGateRoleStatus(
        string role,
        WorkflowReviewGateRoleState state,
        WorkflowReviewVerdict? currentVerdict,
        DateTimeOffset? currentVerdictRecordedAtUtc,
        string? recordedRouteLabel,
        string? recordedEvidenceSummary,
        int staleHashVerdictCount,
        string? newestStaleHash,
        WorkflowReviewVerdict? newestStaleVerdict,
        string detail)
    {
        Role = role;
        State = state;
        CurrentVerdict = currentVerdict;
        CurrentVerdictRecordedAtUtc = currentVerdictRecordedAtUtc;
        RecordedRouteLabel = recordedRouteLabel;
        RecordedEvidenceSummary = recordedEvidenceSummary;
        StaleHashVerdictCount = staleHashVerdictCount;
        NewestStaleHash = newestStaleHash;
        NewestStaleVerdict = newestStaleVerdict;
        Detail = detail;
    }

    /// <summary>The role name exactly as the run's own pinned stage declared it.</summary>
    public string Role { get; }

    /// <summary>What the persisted evidence says about this role on the current verified artifact hash.</summary>
    public WorkflowReviewGateRoleState State { get; }

    /// <summary>The newest verdict for this role on the current hash, or null when there is none.</summary>
    public WorkflowReviewVerdict? CurrentVerdict { get; }

    /// <summary>When that newest verdict was recorded, or null when there is none.</summary>
    public DateTimeOffset? CurrentVerdictRecordedAtUtc { get; }

    /// <summary>
    /// The route string carried by that verdict row, reported strictly as a label written into the row. It
    /// is never presented as an observed model execution: a non-blank value proves only that something was
    /// typed there, and the product stores no reviewer execution that could confirm it.
    /// </summary>
    public string? RecordedRouteLabel { get; }

    /// <summary>The free-text evidence summary stored on that verdict row, if it carried one.</summary>
    public string? RecordedEvidenceSummary { get; }

    /// <summary>How many verdicts this role has on hashes other than the current one.</summary>
    public int StaleHashVerdictCount { get; }

    /// <summary>The newest of those off-hash verdicts' hashes, for naming what went stale.</summary>
    public string? NewestStaleHash { get; }

    /// <summary>The verdict recorded against <see cref="NewestStaleHash"/>.</summary>
    public WorkflowReviewVerdict? NewestStaleVerdict { get; }

    /// <summary>The whole row of evidence for this role, in one line the panel can show.</summary>
    public string Detail { get; }

    /// <summary>
    /// The compact state label rendered on the role's chip.
    /// <para>
    /// The stored verdict values themselves stay untouched in <see cref="Detail"/>, where the operator is
    /// inspecting a persisted row and needs the exact protocol token; the chip is chrome, so it names the
    /// same fact in the operator's own words.
    /// </para>
    /// </summary>
    public string StateDisplay => State switch
    {
        WorkflowReviewGateRoleState.Approved => "одобрено",
        WorkflowReviewGateRoleState.Rejected => "отклонено",
        WorkflowReviewGateRoleState.RequestChanges => "правки",
        WorkflowReviewGateRoleState.ReportedMissing => "вердикта нет",
        WorkflowReviewGateRoleState.Missing => "нет",
        WorkflowReviewGateRoleState.StaleHash => "устаревший хеш",
        WorkflowReviewGateRoleState.EvidenceUnusable => "нечитаемо",
        WorkflowReviewGateRoleState.ArtifactUnverified => "не проверено",
        _ => WorkflowLibraryViewModel.UnavailableIndicator
    };
}

/// <summary>
/// The read-only reviewer-gate status of the observed run's current stage: which roles that stage requires,
/// what the persisted verdicts say about each of them on the current verified artifact hash, and the named
/// reason whenever that cannot be established.
/// </summary>
public sealed record WorkflowReviewGateStatus
{
    private static readonly IReadOnlyList<WorkflowReviewGateRoleStatus> NoRoles = Array.Empty<WorkflowReviewGateRoleStatus>();

    public WorkflowReviewGateStatus(
        WorkflowReviewGateAvailability availability,
        string runId,
        string stageId,
        string? requiredArtifactKind,
        string? currentArtifactHash,
        bool artifactBytesVerified,
        IReadOnlyList<WorkflowReviewGateRoleStatus> roles,
        string explanation)
    {
        Availability = availability;
        RunId = runId;
        StageId = stageId;
        RequiredArtifactKind = requiredArtifactKind;
        CurrentArtifactHash = currentArtifactHash;
        ArtifactBytesVerified = artifactBytesVerified;
        Roles = roles;
        Explanation = explanation;
    }

    /// <summary>Whether the current stage has a gate to report, and what blocks reporting one.</summary>
    public WorkflowReviewGateAvailability Availability { get; }

    /// <summary>The observed run this status describes.</summary>
    public string RunId { get; }

    /// <summary>The run's current stage, as the run itself reports it.</summary>
    public string StageId { get; }

    /// <summary>The artifact kind that stage's pinned definition requires, or null when none was declared.</summary>
    public string? RequiredArtifactKind { get; }

    /// <summary>The hash of the newest stored artifact of the required kind, or null when none is stored.</summary>
    public string? CurrentArtifactHash { get; }

    /// <summary>Whether the committed bytes behind <see cref="CurrentArtifactHash"/> were re-hashed for this observation.</summary>
    public bool ArtifactBytesVerified { get; }

    /// <summary>One entry per required reviewer role of the current stage, in the pinned scheme's own order.</summary>
    public IReadOnlyList<WorkflowReviewGateRoleStatus> Roles { get; }

    /// <summary>The named refusal or unknown state, in the operator's language. Never empty.</summary>
    public string Explanation { get; }

    /// <summary>The status of a screen with nothing observed at all.</summary>
    public static WorkflowReviewGateStatus NoObservedRun { get; } = new(
        WorkflowReviewGateAvailability.NoObservedRun,
        string.Empty,
        string.Empty,
        requiredArtifactKind: null,
        currentArtifactHash: null,
        artifactBytesVerified: false,
        NoRoles,
        "Наблюдаемого запуска нет, поэтому проверка ревьюеров не выполняется.");

    /// <summary>How many roles the current stage requires.</summary>
    public int RequiredRoleCount => Roles.Count;

    /// <summary>How many of those roles are approved on the current verified hash right now.</summary>
    public int ApprovedRoleCount => Roles.Count(role => role.State == WorkflowReviewGateRoleState.Approved);

    /// <summary>
    /// True only when every required role currently holds an <c>Approve</c> on the verified current artifact
    /// hash. This is a statement about the verdict rows on this stage and nothing more: the transition is
    /// still decided separately, and the product never turns it into a claim that the phase is complete.
    /// </summary>
    public bool IsEveryRequiredRoleApproved =>
        Availability == WorkflowReviewGateAvailability.Evaluated
        && ArtifactBytesVerified
        && RequiredRoleCount > 0
        && ApprovedRoleCount == RequiredRoleCount;

    /// <summary>The compact one-line status the panel leads with.</summary>
    public string StateDisplay => Availability switch
    {
        WorkflowReviewGateAvailability.NoObservedRun => WorkflowLibraryViewModel.UnavailableIndicator,
        WorkflowReviewGateAvailability.NoReviewerGate => "ролей не требуется",
        WorkflowReviewGateAvailability.PinnedSchemeUnusable => "схема нечитаема",
        WorkflowReviewGateAvailability.ArtifactRequirementUndeclared => "вид артефакта не объявлен",
        WorkflowReviewGateAvailability.ArtifactMissing => "артефакт не записан",
        WorkflowReviewGateAvailability.ArtifactUnverified => "байты не проверены",
        WorkflowReviewGateAvailability.Evaluated when IsEveryRequiredRoleApproved => "Одобрено у всех ролей",
        WorkflowReviewGateAvailability.Evaluated => string.Create(
            CultureInfo.InvariantCulture,
            $"Одобрено {ApprovedRoleCount}/{RequiredRoleCount}"),
        _ => WorkflowLibraryViewModel.UnavailableIndicator
    };
}

/// <summary>
/// Reads the reviewer-gate status of an observed run, and only that.
///
/// The roles, the current stage and the required artifact kind come from the run's own pinned scheme
/// snapshot and from nowhere else: not from the Studio's edited graph, not from the mutable template, not
/// from a stage display name, not from the process-wide standard scheme and not from typed text. The
/// verdicts come from the run's stored rows, matched to the exact role and the exact hash of the current
/// verified artifact.
///
/// The newest-verdict selection deliberately uses the same expression as the domain transition gate
/// (<c>WorkflowRun.EvaluateTransitionGate</c>): a stable <c>OrderByDescending</c> over
/// <c>RecordedAtUtc</c> over the same list. If this projector chose differently from the gate, the panel
/// could show a role as approved for a transition the run service would then refuse, and a read-only status
/// that disagrees with the authoritative decision is worse than no status at all.
///
/// Nothing here writes. The panel has no verdict-writing control and this type has no way to produce one:
/// a human click must never be able to create model-verdict evidence, so a verdict can only ever arrive
/// here already persisted by a reviewer execution.
/// </summary>
public static class WorkflowReviewGateStatusProjector
{
    /// <summary>
    /// Projects the gate status of <paramref name="run"/>.
    /// <para>
    /// <paramref name="currentArtifactVerified"/> is the outcome of re-hashing the committed bytes of that
    /// run's newest artifact for the required kind. It is passed in rather than computed here so this stays
    /// a pure function and the asynchronous, staleness-guarded call to the blob store stays with the screen
    /// that owns the observation. A null or false value means the bytes are not established as authentic, and
    /// the projection then fails closed: no role is reported as approved.
    /// </para>
    /// </summary>
    public static WorkflowReviewGateStatus Project(WorkflowRun? run, bool? currentArtifactVerified)
    {
        if (run is null)
        {
            return WorkflowReviewGateStatus.NoObservedRun;
        }

        if (!run.IsTemplateBacked || string.IsNullOrWhiteSpace(run.TemplateSchemeSnapshotJson))
        {
            return Refuse(
                WorkflowReviewGateAvailability.PinnedSchemeUnusable,
                run,
                stageId: run.CurrentStageId,
                explanation: $"Запуск '{run.Id}' не закреплён за версией шаблона, поэтому роли проверки ревьюеров для "
                    + "него неприменимы. Роли читаются только из закреплённой схемы самого запуска.");
        }

        WorkflowStageDefinition stage;

        try
        {
            var snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
            var pinnedStage = snapshot.Scheme.FindStage(run.CurrentStageId);

            if (pinnedStage is null)
            {
                return Refuse(
                    WorkflowReviewGateAvailability.PinnedSchemeUnusable,
                    run,
                    stageId: run.CurrentStageId,
                    explanation: $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме запуска '{run.Id}', "
                        + "поэтому проверка ревьюеров не выводится.");
            }

            stage = pinnedStage;
        }
        catch (Exception exception) when (exception is WorkflowValidationException
            or ArgumentException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return Refuse(
                WorkflowReviewGateAvailability.PinnedSchemeUnusable,
                run,
                stageId: run.CurrentStageId,
                explanation: $"Закреплённая схема запуска '{run.Id}' нечитаема, поэтому роли проверки ревьюеров не "
                    + $"выводятся: {UiErrorMessage.Describe(exception)}");
        }

        if (stage.RequiredReviewerRoles.Count == 0)
        {
            return Refuse(
                WorkflowReviewGateAvailability.NoReviewerGate,
                run,
                stageId: stage.StageId,
            explanation: $"Стадия '{stage.StageId}' запуска '{run.Id}' не требует ролей ревьюеров в закреплённой "
                + "схеме, поэтому проверять нечего.");
        }

        if (stage.ArtifactRequirement is null)
        {
            return Refuse(
                WorkflowReviewGateAvailability.ArtifactRequirementUndeclared,
                run,
                stageId: stage.StageId,
            explanation: $"Стадия '{stage.StageId}' запуска '{run.Id}' требует ролей ревьюеров, но не объявляет вид "
                + "артефакта, поэтому вердикты не о чем оценивать.");
        }

        var kind = stage.ArtifactRequirement;
        WorkflowArtifactEvidence? artifact;

        try
        {
            artifact = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stage.StageId, kind);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Refuse(
                WorkflowReviewGateAvailability.ArtifactMissing,
                run,
                stageId: stage.StageId,
                requiredArtifactKind: kind,
                explanation: $"Текущий артефакт стадии '{stage.StageId}' запуска '{run.Id}' не разрешился по сохранённым "
                    + $"строкам, поэтому роли не оцениваются: {UiErrorMessage.Describe(exception)}");
        }

        if (artifact is null)
        {
            return Refuse(
                WorkflowReviewGateAvailability.ArtifactMissing,
                run,
                stageId: stage.StageId,
                requiredArtifactKind: kind,
                explanation: $"Для стадии '{stage.StageId}' запуска '{run.Id}' не записан артефакт вида '{kind}', "
                    + "поэтому ни одна роль ревьюера не может быть оценена.");
        }

        var verified = currentArtifactVerified is true;
        var hash = artifact.HashSha256;
        var roles = stage.RequiredReviewerRoles
            .Select(role => DescribeRole(run, role, hash, verified))
            .ToArray();

        var availability = verified
            ? WorkflowReviewGateAvailability.Evaluated
            : WorkflowReviewGateAvailability.ArtifactUnverified;

        var explanation = verified
            ? $"Роли читаются из закреплённой схемы запуска '{run.Id}'. Оценены по хешу "
                + $"{ReviewGateText.Shorten(hash)}. Сохранённые вердикты не разрешают переход: в этой версии "
                + "не подтверждено происхождение ответа модели для конкретного запуска."
            : $"Байты артефакта {ReviewGateText.Shorten(hash)} не подтверждены хранилищем, поэтому ни одна роль "
                + "не засчитана одобренной, даже если вердикт по этому хешу сохранён.";

        return new WorkflowReviewGateStatus(
            availability,
            run.Id,
            stage.StageId,
            kind,
            hash,
            verified,
            roles,
            explanation);
    }

    /// <summary>
    /// Describes one required role against the exact current hash.
    ///
    /// The newest matching row decides, and a newer row always wins: an <c>Approve</c> followed by a
    /// <c>Reject</c> on the same hash reads as a rejection here, exactly as it does at the gate. When there
    /// is no row on the current hash, the off-hash rows are named rather than quietly ignored, so a stale
    /// approval is visible as stale instead of as a missing one.
    /// </summary>
    private static WorkflowReviewGateRoleStatus DescribeRole(
        WorkflowRun run,
        string role,
        string currentHash,
        bool artifactVerified)
    {
        // The same predicate and the same stable descending order the domain gate uses, over the same list,
        // so the newest row here is exactly the newest row the gate would read. Written as the gate writes
        // it rather than as a hand-rolled scan: a manual "greater than or equal" comparison would resolve a
        // tie between two rows in the opposite order and could show an approval the gate then refuses.
        var onCurrentHash = run.Verdicts
            .Where(verdict => string.Equals(verdict.ReviewerRole, role, StringComparison.Ordinal)
                && string.Equals(verdict.DocumentHash, currentHash, StringComparison.Ordinal))
            .OrderByDescending(verdict => verdict.RecordedAtUtc)
            .ToArray();

        var onOtherHashes = run.Verdicts
            .Where(verdict => string.Equals(verdict.ReviewerRole, role, StringComparison.Ordinal)
                && !string.Equals(verdict.DocumentHash, currentHash, StringComparison.Ordinal))
            .OrderByDescending(verdict => verdict.RecordedAtUtc)
            .ToArray();

        var newestOnCurrentHash = onCurrentHash.FirstOrDefault();
        var newestOffHash = onOtherHashes.FirstOrDefault();
        var staleCount = onOtherHashes.Length;

        if (newestOnCurrentHash is null)
        {
            return staleCount > 0
                ? DescribeStaleRole(role, staleCount, newestOffHash)
                : new WorkflowReviewGateRoleStatus(
                    role,
                    WorkflowReviewGateRoleState.Missing,
                    currentVerdict: null,
                    currentVerdictRecordedAtUtc: null,
                    recordedRouteLabel: null,
                    recordedEvidenceSummary: null,
                    staleHashVerdictCount: 0,
                    newestStaleHash: null,
                    newestStaleVerdict: null,
                    detail: "вердиктов по текущему хешу нет; роль не оценена.");
        }

        // A newer unreadable row is not skipped in favour of an older readable approval: the role fails
        // closed, because the row that would decide it cannot be read as evidence at all.
        if (!IsReadableEvidence(newestOnCurrentHash))
        {
            return new WorkflowReviewGateRoleStatus(
                role,
                WorkflowReviewGateRoleState.EvidenceUnusable,
                currentVerdict: newestOnCurrentHash.Verdict,
                currentVerdictRecordedAtUtc: newestOnCurrentHash.RecordedAtUtc,
                recordedRouteLabel: Redact(newestOnCurrentHash.RouteId),
                recordedEvidenceSummary: null,
                staleHashVerdictCount: staleCount,
                newestStaleHash: newestOffHash?.DocumentHash,
                newestStaleVerdict: newestOffHash?.Verdict,
                detail: "самая свежая сохранённая строка по текущему хешу нечитаема как доказательство, поэтому "
                    + "роль не оценена и более ранняя строка не подставляется вместо неё.");
        }

        var recorded = ReviewGateText.DescribeVerdict(
            newestOnCurrentHash.Verdict,
            newestOnCurrentHash.RecordedAtUtc,
            newestOnCurrentHash.RouteId);

        if (!artifactVerified)
        {
            return new WorkflowReviewGateRoleStatus(
                role,
                WorkflowReviewGateRoleState.ArtifactUnverified,
                newestOnCurrentHash.Verdict,
                newestOnCurrentHash.RecordedAtUtc,
                newestOnCurrentHash.RouteId,
                newestOnCurrentHash.EvidenceSummary,
                staleCount,
                newestOffHash?.DocumentHash,
                newestOffHash?.Verdict,
                $"сохранённый вердикт по текущему хешу: {recorded}; байты артефакта не проверены, роль не засчитана");
        }

        var state = newestOnCurrentHash.Verdict switch
        {
            WorkflowReviewVerdict.Approve => WorkflowReviewGateRoleState.Approved,
            WorkflowReviewVerdict.Reject => WorkflowReviewGateRoleState.Rejected,
            WorkflowReviewVerdict.RequestChanges => WorkflowReviewGateRoleState.RequestChanges,
            _ => WorkflowReviewGateRoleState.ReportedMissing
        };

        return new WorkflowReviewGateRoleStatus(
            role,
            state,
            newestOnCurrentHash.Verdict,
            newestOnCurrentHash.RecordedAtUtc,
            newestOnCurrentHash.RouteId,
            newestOnCurrentHash.EvidenceSummary,
            staleCount,
            newestOffHash?.DocumentHash,
            newestOffHash?.Verdict,
            $"вердикт по текущему хешу: {recorded}");
    }

    private static WorkflowReviewGateRoleStatus DescribeStaleRole(
        string role,
        int staleCount,
        ReviewerVerdictRecord? newestStale) =>
        new(
            role,
            WorkflowReviewGateRoleState.StaleHash,
            currentVerdict: null,
            currentVerdictRecordedAtUtc: null,
            recordedRouteLabel: null,
            recordedEvidenceSummary: null,
            staleCount,
            newestStale?.DocumentHash,
            newestStale?.Verdict,
            $"вердиктов по текущему хешу нет; по другим хешам сохранено: {staleCount}"
                + (newestStale is null
                    ? string.Empty
                    : $", последний вердикт '{newestStale.Verdict}' по {ReviewGateText.Shorten(newestStale.DocumentHash)}")
                + " — прежний хеш артефакта больше не авторизует ничего.");

    /// <summary>
    /// Whether a persisted verdict row can be reported as evidence at all. A row whose role or route is
    /// blank, or whose hash is not a strict content hash, cannot be trusted to describe what it claims to
    /// describe, so the panel refuses to read it rather than repeating it.
    /// </summary>
    private static bool IsReadableEvidence(ReviewerVerdictRecord verdict) =>
        !string.IsNullOrWhiteSpace(verdict.ReviewerRole)
        && !string.IsNullOrWhiteSpace(verdict.RouteId)
        && WorkflowArtifactEvidence.IsContentHash(verdict.DocumentHash);

    private static string? Redact(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static WorkflowReviewGateStatus Refuse(
        WorkflowReviewGateAvailability availability,
        WorkflowRun run,
        string stageId,
        string explanation,
        string? requiredArtifactKind = null) =>
        new(
            availability,
            run.Id,
            stageId,
            requiredArtifactKind,
            currentArtifactHash: null,
            artifactBytesVerified: false,
            WorkflowReviewGateStatus.NoObservedRun.Roles,
            explanation);
}

/// <summary>
/// Wording shared by the projector and the panel that shows it. The route is always described as a label
/// written into the stored row, because the product persists no reviewer execution that could confirm it.
/// </summary>
internal static class ReviewGateText
{
    /// <summary>
    /// Shortens a content hash for a compact row while keeping it recognisable, and leaves anything that is
    /// not already short enough to read alone.
    /// </summary>
    public static string Shorten(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "нет";
        }

        return hash.Length <= 24 ? hash : string.Concat(hash.AsSpan(0, 21), "…");
    }

    /// <summary>
    /// Describes one stored verdict. The route is named as a recorded label and nothing more: a non-blank
    /// <see cref="ReviewerVerdictRecord.RouteId"/> proves only that the string was stored, never that a model
    /// execution was observed, and the wording has to keep that distinction on screen.
    /// <para>
    /// The verdict token and the route string are quoted verbatim on purpose. This row is the one place the
    /// operator inspects the identifiers a reviewer execution actually persisted, so they are reproduced
    /// exactly rather than translated into the Russian label the role chip carries.
    /// </para>
    /// </summary>
    public static string DescribeVerdict(WorkflowReviewVerdict verdict, DateTimeOffset recordedAtUtc, string routeId)
    {
        var recordedAt = recordedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"вердикт '{verdict}' от {recordedAt} · маршрут в строке: '{routeId}' (запись, не наблюдение)");
    }
}
