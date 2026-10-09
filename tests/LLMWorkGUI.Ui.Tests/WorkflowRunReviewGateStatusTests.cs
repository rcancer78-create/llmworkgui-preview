using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Evidence for the read-only reviewer-gate status of the product run console (ROADMAP 10K).
/// <para>
/// The whole path runs against the real composition over a migrated SQLite database: the shipped Workflows
/// screen, the registered run service, the registered blob store and the real loaded
/// <c>ScreenTemplates.xaml</c>. Every assertion is about stored rows or about real controls, never about a
/// view model that was handed its own object.
/// </para>
/// <para>
/// What these tests pin down is the negative space as much as the happy path. Two required roles are read
/// from the run's own pinned snapshot and nothing else; an approval that is never written is missing; a
/// newer Reject overrides an older Approve on the same hash; an approval stops counting the moment the
/// artifact is replaced; a missing or altered blob leaves no role satisfied; and a newer artifact whose
/// bytes cannot be re-hashed fails the whole gate closed instead of falling back to the older one that
/// still verifies. The panel writes nothing at all, so observing it never produces a verdict row.
/// </para>
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowRunReviewGateStatusTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionOneId = "version-1";
    private const string VersionTwoId = "version-2";
    private const string TemplateId = "review-gate-template";
    private const string FirstStageId = "node-a";
    private const string SecondStageId = "node-b";
    private const string ArtifactKind = "ReviewedDocument";
    private const string ReviewerRole = "Reviewer";
    private const string ArchitectRole = "Architect";

    /// <summary>
    /// A route label that is never observed by anything. It exists so the tests can prove that a non-blank
    /// <c>RouteId</c> is reported as a stored string and not as a model execution. It is not a
    /// <c>Routes</c> row, so no reviewer execution can ever carry it, and no verdict recorded through the run
    /// service can name it.
    /// </summary>
    private const string UnobservedRouteId = "route-never-observed";

    /// <summary>
    /// A real persisted route, seeded alongside the project. A pinned model-review stage is authorized only by
    /// a reviewer execution whose requested and observed routes are a stored <c>Routes</c> row, so this is
    /// what the recorded verdicts in this file are read out of.
    /// </summary>
    private const string ReviewerRouteId = "route-reviewer-ui";
    private const string ReviewerProfileId = "profile-reviewer-ui";
    private const string ReviewerAccountId = "account-reviewer-ui";
    private const string ReviewerModelId = "model-reviewer-ui";

    private static readonly DateTimeOffset SeededAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The reviewer execution minted for one run, stage, role and reviewed hash, reused when that role signs
    /// the same bytes again. Keyed the way the storage layer keys its uniqueness constraint.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ReviewerExecutions =
        new(StringComparer.Ordinal);

    private static readonly DateTimeOffset[] VerdictClock =
    {
        SeededAt.AddMinutes(10),
        SeededAt.AddMinutes(20),
        SeededAt.AddMinutes(30),
        SeededAt.AddMinutes(40)
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "llmworkgui-phase10k-" + Guid.NewGuid().ToString("N"));

    private readonly string _appData;

    private readonly SqliteConnectionFactory _factory;

    public WorkflowRunReviewGateStatusTests()
    {
        _appData = Path.Combine(_root, "appdata");
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));

        using var host = CreateHost();
        HostBootstrapper.InitializeAsync(host).GetAwaiter().GetResult();
        SeedAsync(host.Services).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var directory in new[] { _appData, _root })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory must never turn a reviewer-gate assertion red.
            }
        }
    }

    [Fact]
    public async Task BothRequiredRolesComeFromThePinnedSnapshotAndNothingIsApprovedBeforeAVerdictExists()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var hash = await CurrentHashAsync(host.Services, library);

        var status = library.ReviewGateStatus;

        // The two roles are exactly the ones the run's own pinned scheme declares on its current stage, in
        // the scheme's own order, and nothing is inferred for them.
        Assert.Equal(WorkflowReviewGateAvailability.Evaluated, status.Availability);
        Assert.Equal(FirstStageId, status.StageId);
        Assert.Equal(ArtifactKind, status.RequiredArtifactKind);
        Assert.Equal(hash, status.CurrentArtifactHash);
        Assert.True(status.ArtifactBytesVerified);

        Assert.Equal(new[] { ReviewerRole, ArchitectRole }, status.Roles.Select(role => role.Role).ToArray());
        Assert.All(status.Roles, role => Assert.Equal(WorkflowReviewGateRoleState.Missing, role.State));

        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.Equal(2, status.RequiredRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
        Assert.Equal("Одобрено 0/2", status.StateDisplay);
        Assert.True(library.HasReviewGateRoles);

        // The compact line names the stage, the required kind, the very hash the verdicts are matched to and
        // the fact that those bytes were re-hashed, so the count can never be read against another artifact.
        var display = library.ReviewGateRequirementDisplay;
        Assert.Contains(FirstStageId, display, StringComparison.Ordinal);
        Assert.Contains(ArtifactKind, display, StringComparison.Ordinal);
        Assert.Contains(hash, display, StringComparison.Ordinal);
        Assert.Contains("проверены", display, StringComparison.Ordinal);
        Assert.Contains("Одобрено: 0/2", display, StringComparison.Ordinal);

        // And observing is strictly read-only: no verdict row appeared out of it.
        Assert.Empty(await StoredVerdictsAsync(host.Services));
    }

    [Fact]
    public async Task HistoricalApprovalsAppearInTheProjectionButCannotAuthorizeTheProductGate()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);
        Assert.Equal("Одобрено у всех ролей", library.ReviewGateStatus.StateDisplay);
        Assert.Contains("Одобрено: 2/2", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);

        // Historical rows can describe approvals, but do not prove that a model response was
        // persisted and parsed. The product service must still refuse their use as gate authority.
        Assert.True(library.CanAdvanceObservedRun);
        await library.AdvanceObservedRunAsync();

        Assert.Contains("model response", library.Blocker, StringComparison.Ordinal);
        Assert.Equal(FirstStageId, (await StoredRunAsync(host.Services, runId)).CurrentStageId);
    }

    [Fact]
    public async Task ANewerRejectOnTheSameHashOverridesAnOlderApproveAndTheGateStillRefuses()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);
        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Reject, 2);
        await library.ObserveActiveRunAsync();

        var status = library.ReviewGateStatus;

        // The newest row for the exact role and hash decides, so the role reads as rejected even though an
        // approval for it is still stored one minute earlier.
        Assert.Equal(WorkflowReviewGateRoleState.Rejected, Role(status, ReviewerRole).State);
        Assert.Equal(WorkflowReviewVerdict.Reject, Role(status, ReviewerRole).CurrentVerdict);
        Assert.Equal(VerdictClock[2], Role(status, ReviewerRole).CurrentVerdictRecordedAtUtc);
        Assert.Equal(WorkflowReviewGateRoleState.Approved, Role(status, ArchitectRole).State);

        Assert.Equal(1, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
        Assert.Equal("Одобрено 1/2", status.StateDisplay);

        // And the run service refuses the transition for exactly the reason the panel shows, so the two can
        // never drift apart.
        await library.AdvanceObservedRunAsync();

        Assert.Contains("одобрено 1/2", library.Blocker, StringComparison.Ordinal);
        Assert.Equal(WorkflowReviewGateRoleState.Rejected, Role(library.ReviewGateStatus, ReviewerRole).State);
        Assert.Contains("Approve", library.Blocker, StringComparison.Ordinal);
        Assert.Equal(FirstStageId, (await StoredRunAsync(host.Services, runId)).CurrentStageId);

        await library.ObserveActiveRunAsync();
        Assert.Equal(WorkflowReviewGateRoleState.Rejected, Role(library.ReviewGateStatus, ReviewerRole).State);
    }

    [Fact]
    public async Task AnApproveOnAReplacedArtifactBecomesStaleAndStopsCounting()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var firstHash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, firstHash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, firstHash, WorkflowReviewVerdict.Approve, 1);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        // The artifact is replaced: the stored rows for the new bytes are new, and the decisions the reviewer
        // made are about bytes that are no longer the current ones.
        var secondHash = await RecordReplacementAsync(host.Services, runId, "a different document under review");
        Assert.NotEqual(firstHash, secondHash);

        await library.ObserveActiveRunAsync();

        var status = library.ReviewGateStatus;

        Assert.Equal(WorkflowReviewGateAvailability.Evaluated, status.Availability);
        Assert.Equal(secondHash, status.CurrentArtifactHash);
        Assert.All(status.Roles, role => Assert.Equal(WorkflowReviewGateRoleState.StaleHash, role.State));

        // The stale evidence is named rather than hidden, so an old approval is visible as stale instead of
        // as a role that simply never answered.
        var reviewer = Role(status, ReviewerRole);
        Assert.Equal(1, reviewer.StaleHashVerdictCount);
        Assert.Equal(firstHash, reviewer.NewestStaleHash);
        Assert.Equal(WorkflowReviewVerdict.Approve, reviewer.NewestStaleVerdict);
        Assert.Null(reviewer.CurrentVerdict);
        Assert.Contains("по другим хешам сохранено: 1", reviewer.Detail, StringComparison.Ordinal);
        Assert.Contains(firstHash[..21], reviewer.Detail);

        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);

        await library.AdvanceObservedRunAsync();
        Assert.Equal(FirstStageId, (await StoredRunAsync(host.Services, runId)).CurrentStageId);
    }

    [Fact]
    public async Task NoRoleIsSatisfiedWhenTheCommittedBytesCannotBeReHashed()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        // The rows stay exactly as they were; only the bytes behind them are gone.
        DeleteCommittedBlob(hash);
        await library.ObserveActiveRunAsync();

        var status = library.ReviewGateStatus;

        Assert.Equal(WorkflowReviewGateAvailability.ArtifactUnverified, status.Availability);
        Assert.False(status.ArtifactBytesVerified);
        Assert.Equal(hash, status.CurrentArtifactHash);
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
        Assert.DoesNotContain(
            status.Roles,
            role => role.State == WorkflowReviewGateRoleState.Approved);

        // The verdict is still reported, because it is still stored, but the role is explicitly not counted.
        Assert.All(status.Roles, role => Assert.Equal(WorkflowReviewGateRoleState.ArtifactUnverified, role.State));
        Assert.All(status.Roles, role => Assert.Equal(WorkflowReviewVerdict.Approve, role.CurrentVerdict));
        Assert.All(status.Roles, role => Assert.Contains("роль не засчитана", role.Detail, StringComparison.Ordinal));

        Assert.Contains("не проверены", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        Assert.Contains("байты", library.ReviewGateStateDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANewerUnverifiableArtifactFailsTheGateClosedInsteadOfFallingBackToTheOlderOne()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var firstHash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, firstHash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, firstHash, WorkflowReviewVerdict.Approve, 1);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        // A newer artifact row is recorded and then its bytes are lost, while the older one still verifies.
        // The newest row is the one that authorizes a stage, so the gate has to fail on it rather than
        // quietly keep honouring the older, still-readable artifact.
        var secondHash = await RecordReplacementAsync(host.Services, runId, "the newest artifact, with lost bytes");
        Assert.NotEqual(firstHash, secondHash);
        DeleteCommittedBlob(secondHash);

        await library.ObserveActiveRunAsync();

        var status = library.ReviewGateStatus;

        Assert.Equal(WorkflowReviewGateAvailability.ArtifactUnverified, status.Availability);
        Assert.Equal(secondHash, status.CurrentArtifactHash);
        Assert.False(status.ArtifactBytesVerified);
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.DoesNotContain(status.Roles, role => role.State == WorkflowReviewGateRoleState.Approved);
    }

    [Fact]
    public async Task AHistoricalStageFixtureRederivesTheRolesFromTheNewPinnedStage()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);

        // The helper first proves the product refusal, then traverses the domain fixture only
        // to exercise the read-only projection for a different pinned stage.
        await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(
            host.Services, runId, "Historical setup for stage projection after product refusal.");

        // The new stage has no artifact of its own yet, so nothing about it can be evaluated and the panel
        // says exactly that rather than carrying the previous stage's verdicts forward.
        await library.ObserveActiveRunAsync();

        Assert.Equal(WorkflowReviewGateAvailability.ArtifactMissing, library.ReviewGateStatus.Availability);
        Assert.Empty(library.ReviewGateStatus.Roles);

        await AttachArtifactAsync(library, "the implementation under review");

        // The second stage of the same pinned scheme requires a different role set, and the panel follows the
        // run rather than remembering the roles of the stage it left.
        var status = library.ReviewGateStatus;

        Assert.Equal(SecondStageId, status.StageId);
        Assert.Equal(new[] { "Tester" }, status.Roles.Select(role => role.Role).ToArray());
        Assert.Equal(WorkflowReviewGateRoleState.Missing, status.Roles[0].State);
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.Equal(1, status.RequiredRoleCount);
        Assert.Contains("вердиктов по текущему хешу нет", status.Roles[0].Detail, StringComparison.Ordinal);

        // Neither of the roles that approved the previous stage is part of this gate, so their approvals
        // cannot leak into it.
        Assert.DoesNotContain(status.Roles, role => role.Role == ReviewerRole);
        Assert.DoesNotContain(status.Roles, role => role.Role == ArchitectRole);

        // And the new stage's own artifact and verdict are what satisfies it.
        var secondHash = await CurrentHashAsync(host.Services, library);
        await RecordVerdictAsync(host.Services, runId, "Tester", secondHash, WorkflowReviewVerdict.Approve, 2);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);
        Assert.Contains("Одобрено: 1/1", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStageThatRequiresNoReviewersIsReportedAsHavingNoGate()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignTemplateAsync(host.Services, TemplateId, requiresReviewerGate: false);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var status = library.ReviewGateStatus;

        // "No gate at this stage" is stated as itself. It is not an empty list of satisfied roles and it is
        // not the same word as a gate that passed.
        Assert.Equal(WorkflowReviewGateAvailability.NoReviewerGate, status.Availability);
        Assert.Equal(FirstStageId, status.StageId);
        Assert.Empty(status.Roles);
        Assert.Equal(0, status.RequiredRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
        Assert.Equal("ролей не требуется", status.StateDisplay);
        Assert.False(library.HasReviewGateRoles);
        Assert.Contains("не требует ролей ревьюеров", library.ReviewGateStateDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStageWithNoStoredArtifactIsRefusedByNameRatherThanShownAsEmpty()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignTemplateAsync(host.Services, TemplateId, requiresReviewerGate: true);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var status = library.ReviewGateStatus;

        // There is no artifact to be about yet, so neither role is "missing a verdict": nothing is evaluated.
        Assert.Equal(WorkflowReviewGateAvailability.ArtifactMissing, status.Availability);
        Assert.Empty(status.Roles);
        Assert.Null(status.CurrentArtifactHash);
        Assert.False(status.ArtifactBytesVerified);
        Assert.Contains("не записан артефакт вида", library.ReviewGateStateDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatIsNotPinnedToATemplateIsRefusedByNameAndNoRoleIsInvented()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignTemplateAsync(host.Services, TemplateId, requiresReviewerGate: true);

        // A legacy run carries no pinned scheme snapshot at all. Its roles would have to be guessed from the
        // process-wide standard scheme, so the panel refuses instead of reporting somebody else's roles.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var legacy = await runService.StartLegacyRunAsync(ProjectId, PackageId, VersionTwoId);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        var status = library.ReviewGateStatus;

        Assert.Equal(legacy.Id, status.RunId);
        Assert.Equal(WorkflowReviewGateAvailability.PinnedSchemeUnusable, status.Availability);
        Assert.Empty(status.Roles);
        Assert.Equal("схема нечитаема", status.StateDisplay);
        Assert.Contains("не закреплён за версией шаблона", library.ReviewGateStateDisplay, StringComparison.Ordinal);

        // The process-wide scheme does declare reviewer roles for its own review stages. None of them may
        // leak into a run that never pinned that scheme.
        AssertMentionsNone(library.ReviewGateStateDisplay, ReviewerRole, ArchitectRole, "UiReviewer");
    }

    [Fact]
    public async Task TheRouteInsideAVerdictIsNeverReportedAsAnObservedModelExecution()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await library.ObserveActiveRunAsync();

        var reviewer = Role(library.ReviewGateStatus, ReviewerRole);

        // The string is reported, because it is what the row stores, and it is reported as a stored string -
        // even though a real reviewer execution with a real observed route on a real Routes row is what put
        // the verdict there. A read-only panel is not a second gate: it repeats what the row says and never
        // upgrades that into a claim about a model having produced the verdict.
        Assert.Equal(ReviewerRouteId, reviewer.RecordedRouteLabel);
        Assert.Contains(ReviewerRouteId, reviewer.Detail, StringComparison.Ordinal);
        Assert.Contains("(запись, не наблюдение)", reviewer.Detail, StringComparison.Ordinal);

        // The permanent notice says out loud that the panel itself makes no claim about which model gave the
        // verdict, so nothing on it can be read as one.
        Assert.Contains("не утверждает, какая модель", library.ReviewGateNotice, StringComparison.Ordinal);
        AssertMentionsNone(
            library.ReviewGateStateDisplay,
            "наблюдение",
            "наблюдён",
            "исполнение модели");
    }

    [Fact]
    public async Task ThePanelExposesNoVerdictWritingCommandAndObservationWritesNothing()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);

        // The one and only way a verdict reaches storage in this test is the run service, called directly.
        // The screen itself writes none: no public command names a verdict, and the counts before and after a
        // full re-observation - and after asking for an assigned review - are identical.
        var commands = typeof(WorkflowLibraryViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(commands.Where(name => name.Contains("Verdict", StringComparison.OrdinalIgnoreCase)));

        // The one review-related command that does exist asks the assigned model to review the artifact. It
        // records no verdict - the product's own refusal is what it must report here, because no production
        // channel reports an independently observed route - and it leaves the stored verdicts untouched.
        Assert.Contains("RequestAssignedReviewCommand", commands);

        await library.RequestAssignedReviewAsync();
        await library.ObserveActiveRunAsync();

        // The refusal is per role and named: this template binds no route to either required reviewer role, so
        // both roles are refused by name, nothing was dispatched, and there is no pending reviewer turn behind
        // the refusal for a later read to mistake for one.
        Assert.True(library.AssignedReviewResult!.IsRefusal);
        Assert.Equal(0, library.AssignedReviewResult.DispatchedRoleCount);
        Assert.All(library.AssignedReviewResult.Roles, role => Assert.NotNull(role.Refusal));
        Assert.All(library.AssignedReviewResult.Roles, role => Assert.Null(role.ExecutionId));
        Assert.All(library.AssignedReviewResult.Roles, role => Assert.Null(role.ObservedRouteId));
        Assert.All(
            library.AssignedReviewResult.Roles,
            role => Assert.Contains("binds no route to the required reviewer role", role.Refusal!, StringComparison.Ordinal));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await library.ObserveActiveRunAsync();
        }

        var stored = await StoredVerdictsAsync(host.Services);

        Assert.Equal(2, stored.Count);
        Assert.Equal(
            new[] { ArchitectRole, ReviewerRole },
            stored.Select(verdict => verdict.ReviewerRole).OrderBy(role => role, StringComparer.Ordinal).ToArray());

        // The console is still a live, functioning run console afterwards: nothing about it was disabled.
        Assert.True(library.CanAdvanceObservedRun);
    }

    [Fact]
    public async Task TheReviewRequestOutcomeIsWithdrawnWhenTheObservedRunIsReplacedByAnotherOne()
    {
        // A review-request outcome names the run, the stage, the roles and the hash it was about. Next to
        // another run's button and another run's stage it reads as that run's answer, so the screen has to
        // withdraw it the moment it stops observing the run it belongs to - synchronously, in the same step
        // that adopts the new run, so there is no window in which both are on screen.
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var firstRunId = library.ObservedRun!.Id;

        await library.RequestAssignedReviewAsync();

        var first = library.AssignedReviewResult!;

        Assert.Equal(firstRunId, first.RunId);
        Assert.True(first.IsRefusal);
        Assert.Equal(0, first.DispatchedRoleCount);
        Assert.Contains(firstRunId, library.AssignedReviewStatusDisplay, StringComparison.Ordinal);
        Assert.NotEqual(WorkflowLibraryViewModel.UnavailableIndicator, library.AssignedReviewDetailDisplay);

        var firstStatus = library.AssignedReviewStatusDisplay;
        var firstDetail = library.AssignedReviewDetailDisplay;

        // A refresh of the same run keeps it: the fact that the request really happened is still a fact about
        // the run it happened on, and re-reading a run is not a change of run.
        await library.ObserveActiveRunAsync();
        await library.ObserveActiveRunAsync();

        Assert.Same(first, library.AssignedReviewResult);
        Assert.Equal(firstStatus, library.AssignedReviewStatusDisplay);

        // Run A is closed elsewhere and run B takes its place on the same pinned version.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(firstRunId, "closed by a second run");
        var second = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        await library.ObserveActiveRunAsync();

        Assert.Equal(second.Id, library.ObservedRun!.Id);
        Assert.Null(library.AssignedReviewResult);
        Assert.False(library.HasAssignedReviewResult);
        Assert.NotEqual(firstStatus, library.AssignedReviewStatusDisplay);
        Assert.NotEqual(firstDetail, library.AssignedReviewDetailDisplay);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.AssignedReviewDetailDisplay);
        Assert.DoesNotContain(firstRunId, library.AssignedReviewStatusDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain(firstRunId, library.AssignedReviewDetailDisplay, StringComparison.Ordinal);

        // The same statement on the real shipped controls, because a view-model property the panel is not
        // rendering proves nothing about what an operator can read.
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                var status = (TextBlock)FindControl(root, "LibraryAssignedReviewStatusText");
                var detail = (TextBlock)FindControl(root, "LibraryAssignedReviewDetailText");

                Assert.NotEqual(firstStatus, status.Text);
                Assert.NotEqual(firstDetail, detail.Text);
                Assert.DoesNotContain(firstRunId, status.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(firstRunId, detail.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(FirstStageId, detail.Text, StringComparison.Ordinal);
                Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, detail.Text);
            }
            finally
            {
                window.Close();
            }
        });

        // The panel is still a live run console: run B's own request produces run B's own outcome, and
        // nothing of run A's request is left to be read as B's.
        await AttachArtifactAsync(library, "the second document under review");
        await library.ObserveActiveRunAsync();
        await library.RequestAssignedReviewAsync();

        Assert.Equal(second.Id, library.AssignedReviewResult!.RunId);
        Assert.NotEqual(firstRunId, library.AssignedReviewResult.RunId);
    }

    [Fact]
    public async Task TheReviewRequestOutcomeIsWithdrawnWhenThereIsNoObservedRunAtAll()
    {
        // The other direction into the same state: not a different run but no run. An observation that finds
        // nothing, a selection that moved off the run, or a read that failed all arrive here, and a refusal
        // that outlived them would be a claim about nothing at all.
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var firstRunId = library.ObservedRun!.Id;

        await library.RequestAssignedReviewAsync();

        var firstStatus = library.AssignedReviewStatusDisplay;

        Assert.Contains(firstRunId, firstStatus, StringComparison.Ordinal);
        Assert.True(library.AssignedReviewResult!.IsRefusal);

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(firstRunId, "closed with nothing to replace it");

        await library.ObserveActiveRunAsync();

        Assert.Null(library.ObservedRun);
        Assert.Null(library.AssignedReviewResult);
        Assert.False(library.HasAssignedReviewResult);
        Assert.NotEqual(firstStatus, library.AssignedReviewStatusDisplay);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.AssignedReviewDetailDisplay);
        Assert.DoesNotContain(firstRunId, library.AssignedReviewStatusDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain(firstRunId, library.AssignedReviewDetailDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectionThatMovesWhileTheGateArtifactIsVerifiedNeverResurrectsTheOldStatus()
    {
        var gate = new BlobStoreGate();
        using var host = await CreateInitializedHostAsync(gate);
        var library = await StartGatedRunAsync(host.Services);
        var firstHash = await CurrentHashAsync(host.Services, library);

        Assert.Equal(firstHash, library.ReviewGateStatus.CurrentArtifactHash);

        gate.Arm();

        // Started, not awaited: the observation suspends inside the blob store's verification, which is the
        // only await on the gate path, and hands the dispatcher back.
        StaTestRunner.EnsureApplication();
        var stale = StaTestRunner.Run(() => library.ObserveActiveRunAsync());

        await gate.Entered;

        // While the first observation is suspended, the project's active run is replaced: the first run is
        // closed and a second one is started on the same version. The second run sits on the same gated stage
        // with no artifact of its own, so its reviewer gate is unevaluable - a status that is unmistakably
        // different from the one the suspended observation is still holding.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(library.ObservedRun!.Id, "closed elsewhere while the panel was verifying");
        var replacement = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        // This observation needs no verification of its own, so it completes and commits the newer status.
        await StaTestRunner.Run(() => library.ObserveActiveRunAsync());

        Assert.Equal(replacement.Id, library.ReviewGateStatus.RunId);
        Assert.Equal(WorkflowReviewGateAvailability.ArtifactMissing, library.ReviewGateStatus.Availability);

        gate.Open();
        await stale;
        StaTestRunner.Run(DrainDispatcher);

        // The suspended observation carries an older token, so it must not overwrite the newer one: the panel
        // stays on the replacement run instead of being restored to the closed run's hash and roles.
        Assert.Equal(replacement.Id, library.ReviewGateStatus.RunId);
        Assert.Equal(WorkflowReviewGateAvailability.ArtifactMissing, library.ReviewGateStatus.Availability);
        Assert.Empty(library.ReviewGateRoles);
        Assert.DoesNotContain(firstHash, library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShippedReviewGatePanelShowsBothRolesTheHashAndTheNoticeOnRealControls()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await library.ObserveActiveRunAsync();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                var requirement = (TextBlock)FindControl(root, "LibraryReviewGateRequirementText");
                var state = (TextBlock)FindControl(root, "LibraryReviewGateStateText");
                var notice = (TextBlock)FindControl(root, "LibraryReviewGateNoticeText");
                var roles = (ItemsControl)FindControl(root, "LibraryReviewGateRoles");

                // The compact status, the named state and the contract are all real, named controls of the
                // shipped template, and all bound to the view model.
                Assert.Equal("ReviewGateRequirementDisplay", BindingPathOf(requirement, TextBlock.TextProperty));
                Assert.Equal("ReviewGateStateDisplay", BindingPathOf(state, TextBlock.TextProperty));
                Assert.Equal("ReviewGateNotice", BindingPathOf(notice, TextBlock.TextProperty));
                Assert.Equal("ReviewGateRoles", BindingPathOf(roles, ItemsControl.ItemsSourceProperty));

                Assert.Contains(hash, requirement.Text, StringComparison.Ordinal);
                Assert.Contains("Одобрено: 1/2", requirement.Text, StringComparison.Ordinal);
                Assert.Contains(ArtifactKind, requirement.Text, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(state.Text));
                Assert.Contains("не утверждает, какая модель", notice.Text, StringComparison.Ordinal);

                // Both roles really are generated rows of the shipped ItemsControl, each with its own chip.
                var rows = FindVisualDescendants<FrameworkElement>(roles)
                    .OfType<Grid>()
                    .Where(grid => grid.DataContext is WorkflowReviewGateRoleStatus)
                    .ToArray();

                Assert.Equal(2, rows.Length);

                var texts = ReadTextBlocks(roles);

                Assert.Contains(ReviewerRole, texts);
                Assert.Contains(ArchitectRole, texts);
                Assert.Contains("одобрено", texts);
                Assert.Contains("нет", texts);
                Assert.Contains("(запись, не наблюдение)", string.Join(" | ", texts), StringComparison.Ordinal);

                // The panel is read-only in the strongest sense available to a test: there is no button and
                // no editable control anywhere inside it, so no click here can produce a verdict.
                Assert.Empty(FindVisualDescendants<ButtonBase>(roles));
                Assert.Empty(FindVisualDescendants<TextBox>(roles));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task TheShippedReviewGatePanelStaysCompactAndIsCapturedWithItsTwoRoles()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(host.Services, library);

        await RecordVerdictAsync(host.Services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(host.Services, runId, ArchitectRole, hash, WorkflowReviewVerdict.RequestChanges, 1);
        await library.ObserveActiveRunAsync();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            // Captured in the ordinary shipped window. The run, artifact, decision and reviewer panels now
            // live in a full-width band under the library, so nothing has to be rendered at an artificial
            // height to get all of them into one picture.
            var (window, root) = OpenTemplatePanel(library, height: 800, show: false);

            try
            {
                var roles = (ItemsControl)FindControl(root, "LibraryReviewGateRoles");

                // Compact means compact: the role list is laid out at a real size, it is not clipped by the
                // column around it, and two roles take a small fraction of the panel they live in rather
                // than stretching to fill it. A few device pixels of slack is allowed because WPF rounds a
                // desired height down to the pixel grid; anything more would be real clipping.
                const double DevicePixelSlack = 4;

                Assert.True(roles.ActualWidth > 0, "The role list must be laid out at a real width.");
                Assert.True(roles.ActualHeight > 0, "The role list must be laid out at a real height.");
                Assert.True(
                    roles.ActualHeight + DevicePixelSlack >= roles.DesiredSize.Height,
                    $"The role list is clipped: actual {roles.ActualHeight} against desired {roles.DesiredSize.Height}.");
                Assert.True(
                    roles.ActualHeight < 250,
                    $"The role list is not compact: {roles.ActualHeight}px for two roles in the shipped 1280x800 band.");

                var rowHeights = FindVisualDescendants<FrameworkElement>(roles)
                    .OfType<Grid>()
                    .Where(grid => grid.DataContext is WorkflowReviewGateRoleStatus)
                    .Select(grid => grid.ActualHeight)
                    .ToArray();

                Assert.Equal(2, rowHeights.Length);
                Assert.All(rowHeights, height => Assert.True(height > 0, "Each role row must be laid out."));

                var texts = ReadTextBlocks(roles);
                Assert.Contains("одобрено", texts);
                Assert.Contains("правки", texts);

                var screenshotPath = CaptureScreenshot(root, "workflow_product_review_gate.png");

                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The shipped screen at its default viewport. Everything the operator has to reach for a running
    /// stage - the run and its gate, the current-stage artifact input, the assigned-review action and the user
    /// decision - is a laid-out control inside 1280x800, and each is still the view model's own command.
    /// <para>
    /// "Reachable" here means realized and positioned, not merely present in the XAML: a collapsed or
    /// off-screen control can be driven by a test and still be unreachable by a person, which is exactly the
    /// failure this composition exists to remove.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark, 96.0)]
    [InlineData(AppTheme.Light, 96.0)]
    [InlineData(AppTheme.Dark, 192.0)]
    public async Task TheShippedWorkflowsScreenOffersTheRunGateArtifactReviewAndDecisionAtTheShippedWindowSize(
        AppTheme theme,
        double dpi)
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartGatedRunAsync(host.Services);

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);

            var (window, root) = OpenTemplatePanel(library, height: 800);

            try
            {
                var strip = FindControl(root, "LibraryRunStatusStrip");

                // The run and its gate are answered by one strip that never scrolls away, from the same
                // observed run the panels act on.
                Assert.True(strip.IsVisible, "The run status strip must be on screen.");

                var stripTexts = ReadTextBlocks(strip);

                Assert.Contains(library.ObservedRun!.Id, string.Join(" | ", stripTexts), StringComparison.Ordinal);
                Assert.Contains(FirstStageId, string.Join(" | ", stripTexts), StringComparison.Ordinal);
                Assert.Contains(ArtifactKind, string.Join(" | ", stripTexts), StringComparison.Ordinal);
                Assert.Contains("Одобрено: 0/2", string.Join(" | ", stripTexts), StringComparison.Ordinal);

                // The run, the gate, the current-stage artifact requirement and the reason the start action
                // is unavailable are answered above the fold, without any scrolling.
                foreach (var name in new[]
                {
                    "LibraryStartRunButton",
                    "LibraryAdvanceRunButton",
                    "LibraryStageArtifactPathBox",
                    "LibraryAttachStageArtifactButton",
                    "LibraryRequestAssignedReviewButton",
                    "LibraryApproveObservedArtifactButton",
                    "LibraryRejectObservedArtifactButton",
                    "LibraryReviewGateRoles",
                    "LibraryReviewGateRequirementText"
                })
                {
                    var control = FindControl(root, name);

                    Assert.True(control.IsVisible, $"'{name}' must not be collapsed or hidden.");
                    Assert.True(control.ActualWidth > 0, $"'{name}' must be laid out at a real width.");
                    Assert.True(control.ActualHeight > 0, $"'{name}' must be laid out at a real height.");
                    Assert.True(
                        IsInside(control, root),
                        $"'{name}' must sit inside the {root.ActualWidth}x{root.ActualHeight} window, "
                            + $"not below or past its edge.");
                }

                // Nothing is hidden behind a tab, a fold or an expander: the band is a plain scroll region
                // that the operator can still reach, and it is the only thing on this screen that scrolls.
                var band = Assert.IsType<ScrollViewer>(FindControl(root, "LibraryRunActionsScroll"));

                Assert.Equal(ScrollBarVisibility.Auto, band.VerticalScrollBarVisibility);

                // The whole run surface does not fit in one 1280x800 screen - the per-role refusal text and the
                // panel notices are prose - but it is a bounded, labelled, three-column region rather than an
                // opaque page, and this pins how much of it is below the fold so a regression that makes the
                // run surface twice as long cannot pass unnoticed.
                Assert.True(
                    band.ScrollableHeight is > 0 and <= 420,
                    $"The run band needs {band.ScrollableHeight}px of scrolling at 1280x800.");

                // The actions are the product view model's own commands, not new controls invented for the
                // new layout, and the duplicate start is unoffered with the run named as the reason.
                Assert.Equal(
                    "StartAssignedRunCommand",
                    BindingPathOf(FindControl(root, "LibraryStartRunButton"), ButtonBase.CommandProperty));
                Assert.Equal(
                    "AdvanceObservedRunCommand",
                    BindingPathOf(FindControl(root, "LibraryAdvanceRunButton"), ButtonBase.CommandProperty));
                Assert.Equal(
                    "AttachStageArtifactCommand",
                    BindingPathOf(FindControl(root, "LibraryAttachStageArtifactButton"), ButtonBase.CommandProperty));
                Assert.Equal(
                    "RequestAssignedReviewCommand",
                    BindingPathOf(FindControl(root, "LibraryRequestAssignedReviewButton"), ButtonBase.CommandProperty));
                Assert.Equal(
                    "ApproveObservedArtifactCommand",
                    BindingPathOf(FindControl(root, "LibraryApproveObservedArtifactButton"), ButtonBase.CommandProperty));
                Assert.Equal(
                    "RejectObservedArtifactCommand",
                    BindingPathOf(FindControl(root, "LibraryRejectObservedArtifactButton"), ButtonBase.CommandProperty));

                var startButton = (Button)FindControl(root, "LibraryStartRunButton");

                Assert.False(startButton.IsEnabled);
                Assert.Equal(
                    "StartAssignedRunUnavailableReason",
                    BindingPathOf(FindControl(root, "LibraryStartRunReasonText"), TextBlock.TextProperty));
                Assert.Contains(
                    library.ObservedRun!.Id,
                    ((TextBlock)FindControl(root, "LibraryStartRunReasonText")).Text,
                    StringComparison.Ordinal);

                // The advance action of the very same run is untouched by the duplicate-start condition.
                Assert.True(((Button)FindControl(root, "LibraryAdvanceRunButton")).IsEnabled);

                // The remaining input controls are laid out at a real size and simply scrolled to; nothing is
                // collapsed, so none of them can become unreachable.
                foreach (var name in new[]
                {
                    "LibraryStageArtifactClassificationPicker",
                    "LibraryUserApprovalCommentBox"
                })
                {
                    var control = FindControl(root, name);

                    Assert.True(control.IsVisible, $"'{name}' must not be collapsed or hidden.");
                    Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0, $"'{name}' has no size.");
                }

                // The library row lost the run panels and would otherwise clip its own last row of binding
                // actions, so it is a bounded scroll region too and every binding action stays reachable.
                // It also keeps a floor, so the run band can never squeeze the packages, the versions and
                // the project binding off the screen.
                var browser = Assert.IsType<ScrollViewer>(FindControl(root, "LibraryBrowserScroll"));
                Assert.Equal(ScrollBarVisibility.Auto, browser.VerticalScrollBarVisibility);
                Assert.True(
                    browser.ActualHeight >= 150,
                    $"The library region is squeezed to {browser.ActualHeight}px by the run band.");

                foreach (var name in new[]
                {
                    "LibraryProjectPicker",
                    "LibraryBindButton",
                    "LibrarySetActiveVersionButton",
                    "LibraryAdaptButton",
                    "LibraryRollbackButton",
                    "LibraryUnbindButton",
                    "LibraryRefreshButton"
                })
                {
                    var control = FindControl(root, name);

                    Assert.True(control.IsVisible, $"'{name}' must not be collapsed or hidden.");
                    Assert.True(
                        control.ActualWidth > 0 && control.ActualHeight > 0,
                        $"'{name}' must not be clipped away by the library row.");
                }

                var suffix = dpi > 96 ? $"_{(int)dpi}dpi" : string.Empty;

                var screenshotPath = CaptureScreenshot(
                    root,
                    $"workflows_screen_run_actions_{theme.ToString().ToLowerInvariant()}{suffix}.png",
                    dpi);

                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThePanelShipsWithoutACompositionAndSaysWhy()
    {
        var library = new WorkflowLibraryViewModel();

        Assert.Equal(WorkflowReviewGateAvailability.NoObservedRun, library.ReviewGateStatus.Availability);
        Assert.Empty(library.ReviewGateRoles);
        Assert.False(library.HasReviewGateRoles);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ReviewGateStatus.StateDisplay);
        Assert.Contains("Наблюдаемого запуска нет", library.ReviewGateStateDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchToAnotherVersionWhoseActiveQueryReturnsNothingDropsTheSatisfiedGate()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartSatisfiedGatedRunAsync(host.Services);
        var hash = await CurrentHashAsync(host.Services, library);

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);
        Assert.Contains("Одобрено: 2/2", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        Assert.Equal(2, library.ReviewGateRoles.Count);

        // The run is closed outside this window, so the project really has no active run left to observe and
        // the repository says so itself rather than the test assuming it.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(library.ObservedRun!.Id, "closed elsewhere");

        Assert.Null(await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetActiveByProjectIdAsync(ProjectId));

        // A different, real version of the same package is then selected by hand. The console observes a run
        // only for the selected version, so the previous run's gate has to leave the screen along with it
        // rather than keep reporting two approvals against a run nobody is looking at any more.
        var otherVersion = library.Versions.Single(item => item.Id == VersionOneId);

        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() => { library.SelectedVersion = otherVersion; });
        StaTestRunner.Run(DrainDispatcher);

        Assert.Null(library.ObservedRun);
        Assert.False(library.HasObservedRun);
        AssertGateIsInvalidated(library, hash);
        AssertShippedPanelNoLongerShowsTheGate(library, hash);
    }

    [Fact]
    public async Task ARefreshWhoseActiveRunIsGoneOrBelongsToAnotherVersionDropsTheSatisfiedGate()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartSatisfiedGatedRunAsync(host.Services);
        var hash = await CurrentHashAsync(host.Services, library);

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(library.ObservedRun!.Id, "closed elsewhere");

        // The run disappears: an observation that finds no active run at all invalidates the gate.
        await library.ObserveActiveRunAsync();

        Assert.Null(library.ObservedRun);
        AssertGateIsInvalidated(library, hash);

        // Then a real, active run of another version takes the project's place, so the query does return a run
        // - one the selected version does not own. A returned-but-foreign run has to invalidate the gate
        // exactly like an absent one, and a refresh is one of the two triggers of that observation.
        var foreign = await runService.StartRunAsync(ProjectId, PackageId, VersionOneId);
        await library.RefreshAsync();

        Assert.Empty(library.Blocker);
        Assert.Null(library.ObservedRun);
        AssertGateIsInvalidated(library, hash);
        Assert.DoesNotContain(foreign.Id, library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        AssertShippedPanelNoLongerShowsTheGate(library, hash);
    }

    [Fact]
    public async Task ASuspendedObservationOfAClosedRunNeverRestoresTheSatisfiedGateItWasComputing()
    {
        var gate = new BlobStoreGate();
        using var host = await CreateInitializedHostAsync(gate);
        var library = await StartSatisfiedGatedRunAsync(host.Services);
        var hash = await CurrentHashAsync(host.Services, library);

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        gate.Arm();

        // Started, not awaited: the observation suspends inside the blob store's verification of the very
        // artifact both approvals are pinned to, and hands the dispatcher back.
        StaTestRunner.EnsureApplication();
        var suspended = StaTestRunner.Run(() => library.ObserveActiveRunAsync());

        await gate.Entered;

        // While it is suspended, the run is closed elsewhere. The next observation finds no active run, so it
        // invalidates the gate and needs no verification of its own to complete.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(library.ObservedRun!.Id, "closed elsewhere while the panel was verifying");
        await StaTestRunner.Run(() => library.ObserveActiveRunAsync());

        Assert.Equal(WorkflowReviewGateAvailability.NoObservedRun, library.ReviewGateStatus.Availability);

        gate.Open();
        await suspended;
        StaTestRunner.Run(DrainDispatcher);

        // The suspended observation carries the older token, so the projection it finishes computing - which
        // is about a closed run and is internally perfectly consistent - must not be restored over the
        // invalidation, and neither may the two approvals and the hash it would put back on screen.
        Assert.Null(library.ObservedRun);
        AssertGateIsInvalidated(library, hash);
        AssertShippedPanelNoLongerShowsTheGate(library, hash);
    }

    [Fact]
    public void VerifiedArtifactStatusExplainsTheCurrentModelOriginLimitation()
    {
        var run = PinnedRun("node-a", ArtifactKind, new[] { ReviewerRole });
        var artifact = TestArtifact(run.Id, "node-a", ArtifactKind);
        var status = WorkflowReviewGateStatusProjector.Project(run.WithArtifact(artifact), currentArtifactVerified: true);
        Assert.Contains("не разрешают переход", status.Explanation, StringComparison.Ordinal);
        Assert.Contains("происхождение ответа модели", status.Explanation, StringComparison.Ordinal);
        Assert.Equal(WorkflowReviewGateRoleState.Missing, Assert.Single(status.Roles).State);
    }

    [Fact]
    public void AVerdictOnAHashThatIsNotTheCurrentArtifactCanNeverTextMatchItsWayIntoApproval()
    {
        // The run service refuses a verdict whose hash is not the current artifact, so a row like this can only
        // be produced by a writer that does not go through it. The panel still has to read it, and read it as
        // off-hash evidence rather than as a current approval: a near-miss hash and a bare digest that merely
        // contains the real one are both matched by exact ordinal equality and nothing else.
        var run = PinnedRun("node-a", ArtifactKind, new[] { ReviewerRole, ArchitectRole });
        var artifact = TestArtifact(run.Id, "node-a", ArtifactKind);

        var content = Encoding.UTF8.GetBytes("pure projector content");
        var hash = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()}";

        run.RecordReviewerVerdict(LinkedVerdict(
            ReviewerRole, UnobservedRouteId, hash.ToUpperInvariant() + " ", WorkflowReviewVerdict.Approve, "near miss", SeededAt));
        run.RecordReviewerVerdict(LinkedVerdict(
            ArchitectRole, UnobservedRouteId, hash[7..], WorkflowReviewVerdict.Approve, "bare digest", SeededAt.AddMinutes(1)));

        var status = WorkflowReviewGateStatusProjector.Project(run.WithArtifact(artifact), currentArtifactVerified: true);

        Assert.Equal(WorkflowReviewGateAvailability.Evaluated, status.Availability);
        Assert.Equal(hash, status.CurrentArtifactHash);
        Assert.All(status.Roles, role => Assert.Equal(WorkflowReviewGateRoleState.StaleHash, role.State));
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
    }

    [Fact]
    public void AVerdictThatReportsNothingIsStoredEvidenceButNotAnApproval()
    {
        // <c>Missing</c> is a declared verdict value, so a reviewer can persist "I reported nothing" against
        // the current hash. That is a real row, not an absent one, and it is certainly not an approval.
        var run = PinnedRun("node-a", ArtifactKind, new[] { ReviewerRole, ArchitectRole });
        var artifact = TestArtifact(run.Id, "node-a", ArtifactKind);

        run.RecordReviewerVerdict(LinkedVerdict(
            ReviewerRole,
            UnobservedRouteId,
            artifact.HashSha256,
            WorkflowReviewVerdict.Missing,
            "no opinion recorded",
            SeededAt));

        var status = WorkflowReviewGateStatusProjector.Project(run.WithArtifact(artifact), currentArtifactVerified: true);

        Assert.Equal(WorkflowReviewGateAvailability.Evaluated, status.Availability);
        Assert.Equal(WorkflowReviewGateRoleState.ReportedMissing, status.Roles[0].State);
        Assert.Equal(WorkflowReviewGateRoleState.Missing, status.Roles[1].State);
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
    }

    /// <summary>
    /// A verdict row that names the execution, stage and artifact it was read out of, for the projector tests
    /// that build a run in memory rather than through the run service.
    /// <para>
    /// The panel never resolves the execution - it reads stored rows and nothing else - so what matters here
    /// is only that the row is a complete record. The route stays a label written into the row, which is
    /// exactly the distinction the panel is required to keep.
    /// </para>
    /// </summary>
    private static ReviewerVerdictRecord LinkedVerdict(
        string role,
        string routeId,
        string documentHash,
        WorkflowReviewVerdict verdict,
        string evidence,
        DateTimeOffset recordedAtUtc) =>
        new(
            role,
            routeId,
            documentHash,
            verdict,
            evidence,
            recordedAtUtc,
            $"execution-{role}",
            FirstStageId,
            $"artifact-{role}");

    [Fact]
    public void TheProjectorIsPureAndSafeOnARunWithNoArtifactsAtAll()
    {
        var run = PinnedRun("node-a", ArtifactKind, new[] { ReviewerRole });

        var status = WorkflowReviewGateStatusProjector.Project(run, currentArtifactVerified: true);

        Assert.Equal(WorkflowReviewGateAvailability.ArtifactMissing, status.Availability);
        Assert.Empty(status.Roles);
        Assert.Null(status.CurrentArtifactHash);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, WorkflowReviewGateStatusProjector
            .Project(run: null, currentArtifactVerified: true).StateDisplay);
    }

    private static WorkflowReviewGateRoleStatus Role(WorkflowReviewGateStatus status, string role) =>
        Assert.Single(status.Roles.Where(candidate => candidate.Role == role));

    /// <summary>
    /// Asserts that a surface names none of the given strings. Used where a claim has to be absent in every
    /// one of its possible phrasings, which a single substring assertion cannot express.
    /// </summary>
    private static void AssertMentionsNone(string surface, params string[] forbidden)
    {
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Starts a run whose current pinned stage really is a two-role reviewer gate over a real artifact, so
    /// the status under test has verified committed content and a pinned scheme to be read from.
    /// </summary>
    private static async Task<WorkflowLibraryViewModel> StartGatedRunAsync(IServiceProvider services)
    {
        await AssignTemplateAsync(services, TemplateId, requiresReviewerGate: true);

        var library = CreateLibrary(services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        Assert.Equal(FirstStageId, library.ObservedRun!.CurrentStageId);

        await AttachArtifactAsync(library, "the document under review");

        Assert.Single(library.ObservedRun!.Artifacts);

        return library;
    }

    /// <summary>
    /// A gated run that is then fully approved on its current verified hash, so the panel is left in the exact
    /// state a clearing regression has to be able to lose: both roles approved, a real hash, a real stage.
    /// </summary>
    private static async Task<WorkflowLibraryViewModel> StartSatisfiedGatedRunAsync(IServiceProvider services)
    {
        var library = await StartGatedRunAsync(services);
        var runId = library.ObservedRun!.Id;
        var hash = await CurrentHashAsync(services, library);

        await RecordVerdictAsync(services, runId, ReviewerRole, hash, WorkflowReviewVerdict.Approve, 0);
        await RecordVerdictAsync(services, runId, ArchitectRole, hash, WorkflowReviewVerdict.Approve, 1);
        await library.ObserveActiveRunAsync();

        Assert.True(library.ReviewGateStatus.IsEveryRequiredRoleApproved);

        return library;
    }

    /// <summary>
    /// The single statement every path that stops observing a run has to be able to make. It is not "an empty
    /// gate": it is the named "no observed run" state, with no run, no stage, no hash, no roles and no count
    /// left over from the run that is no longer observed.
    /// </summary>
    private static void AssertGateIsInvalidated(WorkflowLibraryViewModel library, string previousHash)
    {
        var status = library.ReviewGateStatus;

        Assert.Equal(WorkflowReviewGateAvailability.NoObservedRun, status.Availability);
        Assert.Equal(string.Empty, status.RunId);
        Assert.Equal(string.Empty, status.StageId);
        Assert.Null(status.RequiredArtifactKind);
        Assert.Null(status.CurrentArtifactHash);
        Assert.False(status.ArtifactBytesVerified);
        Assert.Empty(status.Roles);
        Assert.Equal(0, status.RequiredRoleCount);
        Assert.Equal(0, status.ApprovedRoleCount);
        Assert.False(status.IsEveryRequiredRoleApproved);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, status.StateDisplay);

        Assert.Empty(library.ReviewGateRoles);
        Assert.False(library.HasReviewGateRoles);

        Assert.Contains("Наблюдаемого запуска нет", library.ReviewGateStateDisplay, StringComparison.Ordinal);
        Assert.Contains("Одобрено: 0/0", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain(previousHash, library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("Одобрено: 2/2", library.ReviewGateRequirementDisplay, StringComparison.Ordinal);
        AssertMentionsNone(library.ReviewGateRequirementDisplay, previousHash);
    }

    /// <summary>
    /// The same statement on the real shipped controls, because a view-model property the panel is not
    /// rendering proves nothing about what an operator sees. The status line, the named state and the generated
    /// role chips of <c>ScreenTemplates.xaml</c> are all read here.
    /// </summary>
    private static void AssertShippedPanelNoLongerShowsTheGate(WorkflowLibraryViewModel library, string previousHash)
    {
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                var requirement = (TextBlock)FindControl(root, "LibraryReviewGateRequirementText");
                var state = (TextBlock)FindControl(root, "LibraryReviewGateStateText");
                var roles = (ItemsControl)FindControl(root, "LibraryReviewGateRoles");

                Assert.DoesNotContain(previousHash, requirement.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("Одобрено: 2/2", requirement.Text, StringComparison.Ordinal);
                Assert.Contains("Одобрено: 0/0", requirement.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(FirstStageId, requirement.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(ArtifactKind, requirement.Text, StringComparison.Ordinal);

                Assert.Contains("Наблюдаемого запуска нет", state.Text, StringComparison.Ordinal);

                // No role chip is generated at all: the ItemsControl is bound to an empty list, so the two rows
                // that were on screen a moment ago are not merely restyled, they are gone.
                var rows = FindVisualDescendants<FrameworkElement>(roles)
                    .OfType<Grid>()
                    .Where(grid => grid.DataContext is WorkflowReviewGateRoleStatus)
                    .ToArray();

                Assert.Empty(rows);

                var texts = ReadTextBlocks(roles);

                Assert.DoesNotContain(ReviewerRole, texts);
                Assert.DoesNotContain(ArchitectRole, texts);
                Assert.DoesNotContain("одобрено", texts);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task AttachArtifactAsync(WorkflowLibraryViewModel library, string content)
    {
        library.StageArtifactPath = WriteTempFile(content);
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
    }

    private static string WriteTempFile(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "llmworkgui-phase10k-files");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.md");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

        return path;
    }

    private static async Task<string> CurrentHashAsync(IServiceProvider services, WorkflowLibraryViewModel library)
    {
        var run = await StoredRunAsync(services, library.ObservedRun!.Id);
        var stageId = library.ObservedRun.CurrentStageId;
        var kind = library.ReviewGateStatus.RequiredArtifactKind
            ?? throw new InvalidOperationException("The current stage is expected to declare an artifact kind.");

        return WorkflowArtifactEvidence
            .SelectCurrent(run.Artifacts, run.Id, stageId, kind)!
            .HashSha256;
    }

    private static async Task<string> RecordReplacementAsync(
        IServiceProvider services,
        string runId,
        string content)
    {
        var runService = services.GetRequiredService<IWorkflowRunService>();

        var recorded = await runService.RecordStageArtifactAsync(
            runId,
            FirstStageId,
            ArtifactKind,
            new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence
            .SelectCurrent(recorded.Artifacts, runId, FirstStageId, ArtifactKind)!
            .HashSha256;
    }

    /// <summary>
    /// Persists a reviewer verdict the only way one can be persisted: through the registered run service,
    /// called directly. The console has no equivalent call, which is exactly what the other tests assert.
    /// <para>
    /// The service refuses anything that is not backed by a persisted reviewer execution, so this writes one
    /// first - a real session and a real execution on the seeded route, whose observed route is written only
    /// after the requested one and is bound to the artifact row and stage the verdict names. A verdict that a
    /// caller could make without that is precisely what the run service no longer accepts.
    /// </para>
    /// </summary>
    private static async Task RecordVerdictAsync(
        IServiceProvider services,
        string runId,
        string role,
        string documentHash,
        WorkflowReviewVerdict verdict,
        int clockIndex)
    {
        var runService = services.GetRequiredService<IWorkflowRunService>();
        var recordedAtUtc = VerdictClock[clockIndex];
        var current = services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId).GetAwaiter().GetResult()!;
        var artifact = WorkflowArtifactEvidence.SelectCurrent(
            current.Artifacts,
            runId,
            current.CurrentStageId,
            ArtifactKind)!;

        var executionId = await PersistReviewerExecutionAsync(
                services,
                runId,
                role,
                current.CurrentStageId,
                artifact,
                recordedAtUtc)
            .ConfigureAwait(false);

        await HistoricalWorkflowReviewFixture.SeedAsync(
            services, runId,
            new ReviewerVerdictRecord(
                role,
                ReviewerRouteId,
                documentHash,
                verdict,
                "Historical caller-authored fixture; not parsed model response evidence.",
                recordedAtUtc,
                executionId,
                current.CurrentStageId,
                artifact.ArtifactId));
    }

    /// <summary>
    /// The run-scoped reviewer execution a verdict is read out of: a real session bound to the seeded route's
    /// own account, profile and model, a real execution whose requested and observed routes are that stored
    /// <c>Routes</c> row, and a durable binding of the execution to this run, stage, role and artifact row.
    /// </summary>
    private static async Task<string> PersistReviewerExecutionAsync(
        IServiceProvider services,
        string runId,
        string role,
        string stageId,
        WorkflowArtifactEvidence artifact,
        DateTimeOffset recordedAtUtc)
    {
        // One reviewer turn serves every verdict that role signs for the same bytes, which is what an
        // approve followed by a reject on one hash is. The storage layer enforces the same rule with a
        // uniqueness constraint, so a second turn for the same run, stage, role and reviewed hash is refused
        // rather than silently stored as a second, indistinguishable piece of evidence.
        var key = $"{runId}|{stageId}|{role}|{artifact.HashSha256}";

        if (ReviewerExecutions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var executionId = Guid.NewGuid().ToString("N");

        await services.GetRequiredService<ISessionRepository>().UpsertAsync(
            new Session(
                sessionId,
                new SessionBinding(
                    BackendType.OpenCode,
                    ReviewerProfileId,
                    ReviewerAccountId,
                    ReviewerModelId,
                    null,
                    null,
                    null),
                ProjectId,
                @"C:\project",
                nativeSessionId: null,
                SessionState.Active,
                ReconciliationOutcome.None,
                CloseReason.None,
                continuationOfSessionId: null,
                forkedFromSessionId: null,
                workflowRunId: runId,
                role,
                executionId,
                createdAt: recordedAtUtc,
                lastEventAt: recordedAtUtc));

        await services.GetRequiredService<IExecutionRepository>().UpsertAsync(
            new Execution(
                executionId,
                sessionId,
                executionId,
                ExecutionState.Succeeded,
                ExecutionFailureReason.None,
                ReviewerRouteId,
                ReviewerRouteId,
                retryOfExecutionId: null,
                processState: null,
                exitCode: 0,
                terminationReason: null,
                Array.Empty<string>(),
                artifact.HashSha256,
                artifact.HashSha256,
                recordedAtUtc,
                recordedAtUtc,
                recordedAtUtc));

        var evidenceRepository = services.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var evidence = new ReviewerExecutionEvidence(
            executionId,
            sessionId,
            runId,
            role,
            stageId,
            ReviewerRouteId,
            observedRouteId: null,
            artifact.ArtifactId,
            artifact.HashSha256,
            isReadOnly: true,
            ExecutionState.Succeeded);

        await evidenceRepository.SaveAsync(evidence);
        await evidenceRepository.UpdateObservedAsync(
            evidence.WithObservedOutcome(ReviewerRouteId, ExecutionState.Succeeded));

        ReviewerExecutions[key] = executionId;

        return executionId;
    }

    /// <summary>
    /// A linear template whose entry stage is a two-role reviewer gate, with a second gated stage behind it
    /// so a stage change can be observed, and a terminal node at the end.
    /// </summary>
    private static async Task AssignTemplateAsync(
        IServiceProvider services,
        string templateId,
        bool requiresReviewerGate)
    {
        var store = services.GetRequiredService<IWorkflowTemplateStore>();

        var firstGate = requiresReviewerGate
            ? new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                new[] { ReviewerRole, ArchitectRole },
                requiresUserApproval: false,
                ArtifactKind)
            : null;

        var secondGate = requiresReviewerGate
            ? new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                new[] { "Tester" },
                requiresUserApproval: false,
                ArtifactKind)
            : null;

        await store.SaveAsync(new WorkflowTemplateDefinition(
            templateId,
            1,
            "Review gate template",
            "A linear template whose entry stage is a reviewer gate.",
            new WorkflowGraph(FirstStageId, new[]
            {
                new WorkflowNodeDefinition(
                    FirstStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: SecondStageId,
                    gateMetadata: firstGate),
                new WorkflowNodeDefinition(
                    SecondStageId,
                    WorkflowNodeKind.Prompt,
                    "Node B",
                    "Role B",
                    successTargetNodeId: "node-c",
                    gateMetadata: secondGate),
                new WorkflowNodeDefinition("node-c", WorkflowNodeKind.TerminalOutcome, "Node C", "Role C")
            }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            SeededAt));

        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-review-gate",
            ProjectId,
            templateId,
            1,
            SeededAt));
    }

    private static WorkflowLibraryViewModel CreateLibrary(IServiceProvider services) =>
        services.GetRequiredService<WorkflowLibraryViewModel>();

    private static async Task<WorkflowRun> StoredRunAsync(IServiceProvider services, string runId) =>
        await services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId)
        ?? throw new InvalidOperationException($"The run '{runId}' is expected to be stored.");

    /// <summary>
    /// Every verdict stored for the seeded project, read through the run repository rather than from a view
    /// model that was handed its own object - so a verdict the console invented would still show up here.
    /// </summary>
    private static async Task<IReadOnlyList<ReviewerVerdictRecord>> StoredVerdictsAsync(IServiceProvider services)
    {
        var runs = await services.GetRequiredService<IWorkflowRunRepository>().GetByProjectIdAsync(ProjectId);

        return runs.SelectMany(run => run.Verdicts).ToArray();
    }

    private void DeleteCommittedBlob(string blobId)
    {
        var hex = blobId["sha256:".Length..];
        var path = Path.Combine(_appData, "blobs", "sha256", hex[..2], hex);

        Assert.True(File.Exists(path), "The committed artifact blob is expected to exist before it is deleted.");
        File.Delete(path);
    }

    private async Task SeedAsync(IServiceProvider services)
    {
        var blobStore = services.GetRequiredService<WorkflowBlobStore>();
        await using (var content = new MemoryStream(CreateImportedWorkflowZip()))
        {
            await blobStore.SaveBlobAsync(content);
        }

        var blobId = WorkflowBlobStore.ComputeBlobId(CreateImportedWorkflowZip());

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($project, 'Review gate project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'ZipArchive', $blob, $blob, $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version1, $package, 1, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version2, $package, 2, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowBindings (Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('4b8c1d02-77aa-4f31-9c05-2f9a0b6d1e44', $project, $package, $version2, NULL, $now, $now);
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($profile, 'Reviewer profile', 'OpenCode', 'PrivateSource', $now, $now);
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($account, $profile, 'Reviewer account', 'Unverified', 'Unknown', $now, $now);
            INSERT INTO Models (
                Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                Health, DiscoveredAtUtc)
            VALUES ($model, 'OpenCode', $profile, 'reviewer-model', 'Reviewer model', 'Unknown', 'Imported',
                    'Unknown', $now);
            INSERT INTO Routes (
                Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health,
                CreatedAtUtc, UpdatedAtUtc)
            VALUES ($route, 'OpenCode', $profile, $account, $model, 'PrivateSource', 'Unknown', $now, $now);
            """;

        command.Parameters.AddWithValue("$project", ProjectId);
        command.Parameters.AddWithValue("$package", PackageId);
        command.Parameters.AddWithValue("$version1", VersionOneId);
        command.Parameters.AddWithValue("$version2", VersionTwoId);
        command.Parameters.AddWithValue("$blob", blobId);
        command.Parameters.AddWithValue("$profile", ReviewerProfileId);
        command.Parameters.AddWithValue("$account", ReviewerAccountId);
        command.Parameters.AddWithValue("$model", ReviewerModelId);
        command.Parameters.AddWithValue("$route", ReviewerRouteId);
        command.Parameters.AddWithValue("$now", "2026-09-29T00:00:00Z");

        await command.ExecuteNonQueryAsync();
    }

    private static byte[] CreateImportedWorkflowZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("README.md").Open());
            writer.Write("Imported workflow package.");
        }

        return buffer.ToArray();
    }

    private IHost CreateHost(BlobStoreGate? gate = null) =>
        HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();

                if (gate is not null)
                {
                    SuspendBlobStoreAt(services, gate);
                }
            })
            .Build();

    private static void SuspendBlobStoreAt(IServiceCollection services, BlobStoreGate gate)
    {
        var product = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IWorkflowArtifactBlobStore))
            ?? throw new InvalidOperationException("The product composition no longer registers IWorkflowArtifactBlobStore.");

        var productFactory = product.ImplementationFactory
            ?? throw new InvalidOperationException(
                "IWorkflowArtifactBlobStore is expected to be registered through its own factory.");

        services.Remove(product);
        services.AddSingleton<IWorkflowArtifactBlobStore>(provider => new GatedArtifactBlobStore(
            (IWorkflowArtifactBlobStore)productFactory(provider),
            gate));
    }

    private async Task<IHost> CreateInitializedHostAsync(BlobStoreGate? gate = null)
    {
        var host = CreateHost(gate);
        await HostBootstrapper.InitializeAsync(host);

        return host;
    }

    private static (Window Window, FrameworkElement Root) OpenTemplatePanel(
        WorkflowLibraryViewModel viewModel,
        double height = 900,
        bool show = true)
    {
        // Keep the declared screen viewport when a hosted runner clamps the native window to its
        // smaller desktop. Assertions still require every action to fit inside this 1280px layout.
        var host = new ContentControl { Content = viewModel, Width = 1280, Height = height };

        var window = new Window
        {
            Width = 1280,
            Height = height,
            Content = host,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml",
                UriKind.Absolute)
        });

        if (show)
        {
            window.Show();
        }

        host.Measure(new Size(1280, height));
        host.Arrange(new Rect(0, 0, 1280, height));
        host.UpdateLayout();

        Assert.Equal(1280, host.ActualWidth);
        Assert.Equal(height, host.ActualHeight);

        return (window, host);
    }

    private static FrameworkElement FindControl(DependencyObject root, string name) =>
        FindVisualDescendants<FrameworkElement>(root).Single(element => element.Name == name);

    /// <summary>
    /// Whether a control's whole box lies inside its ancestor's box. A control that is laid out but pushed
    /// past the window edge is exactly what an operator cannot reach, so a real size is not enough.
    /// </summary>
    private static bool IsInside(FrameworkElement control, FrameworkElement ancestor)
    {
        var origin = control.TransformToAncestor(ancestor).Transform(new Point(0, 0));

        const double Slack = 1;

        return origin.X >= -Slack
            && origin.Y >= -Slack
            && origin.X + control.ActualWidth <= ancestor.ActualWidth + Slack
            && origin.Y + control.ActualHeight <= ancestor.ActualHeight + Slack;
    }

    private static string BindingPathOf(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBindingExpression(element, property)?.ParentBinding.Path.Path ?? string.Empty;

    private static string CaptureScreenshot(FrameworkElement root, string fileName, double dpi = 96) =>
        CaptureScreenshot(root, fileName, (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), dpi);

    private static string CaptureScreenshot(FrameworkElement root, string fileName, int width, int height)
    {
        return CaptureScreenshot(root, fileName, width, height, 96);
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName, int width, int height, double dpi)
    {
        var renderBitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        var screenshotPath = Path.Combine(ScreenshotFile.OutputDirectory, fileName);
        ScreenshotFile.Save(encoder, screenshotPath);

        Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");

        return screenshotPath;
    }

    private static string[] ReadTextBlocks(DependencyObject root) =>
        FindVisualDescendants<TextBlock>(root)
            .Select(textBlock => textBlock.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.SystemIdle,
            new System.Windows.Threading.DispatcherOperationCallback(
                state => ((System.Windows.Threading.DispatcherFrame)state!).Continue = false),
            frame);

        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static WorkflowRun PinnedRun(string stageId, string? artifactKind, IReadOnlyList<string> reviewerRoles)
    {
        var scheme = new WorkflowScheme(
            stageId,
            new[]
            {
                new WorkflowStageDefinition(
                    stageId,
                    "Node A",
                    "Role A",
                    WorkflowStageKind.DocumentReview,
                    reviewerRoles,
                    requiresUserApproval: false,
                    artifactKind,
                    nextStageId: "node-c",
                    failureStageId: null),
                new WorkflowStageDefinition(
                    "node-c",
                    "Node C",
                    "Role C",
                    WorkflowStageKind.Custom,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: null,
                    nextStageId: null,
                    failureStageId: null)
            });

        return WorkflowRun.StartPinnedToTemplate(
            "run-pure",
            ProjectId,
            PackageId,
            VersionTwoId,
            sessionId: null,
            scheme.GetRequiredStage(stageId),
            SeededAt,
            "pure-template",
            1,
            WorkflowGraphSnapshot.Serialize(new WorkflowGraph(stageId, Array.Empty<WorkflowNodeDefinition>())),
            WorkflowSchemeSnapshot.CreateFrom(scheme).Json);
    }

    private static WorkflowArtifactEvidence TestArtifact(string runId, string stageId, string kind)
    {
        var content = Encoding.UTF8.GetBytes("pure projector content");
        var hash = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()}";

        return new WorkflowArtifactEvidence(
            "artifact-pure",
            runId,
            stageId,
            kind,
            hash,
            hash,
            SeededAt,
            content.LongLength,
            DataClassification.PrivateSource);
    }

    /// <summary>
    /// A one-shot suspension point inside the blob store's verification, putting a test exactly where the
    /// panel cannot see the outcome yet: the current artifact hash is known, and the bytes behind it are not.
    /// </summary>
    private sealed class BlobStoreGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Open() => _release.TrySetResult();

        public async Task WaitAsync()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The product blob store with only its verification suspended at the gate. Every other call, and the
    /// whole storage behind them, is the real one.
    /// </summary>
    private sealed class GatedArtifactBlobStore : IWorkflowArtifactBlobStore
    {
        private readonly IWorkflowArtifactBlobStore _inner;
        private readonly BlobStoreGate _gate;

        public GatedArtifactBlobStore(IWorkflowArtifactBlobStore inner, BlobStoreGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task<bool> VerifyAsync(string blobId, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner.VerifyAsync(blobId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Stream?> OpenVerifiedAsync(
            string blobId,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner.OpenVerifiedAsync(blobId, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        public Task<WorkflowArtifactBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(content, cancellationToken);
    }
}
