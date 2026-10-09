using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the Phase 10D Activity Monitor. The view model is exercised over realistic run
/// aggregates and observable run projections only: role schema, the five node statuses, parallel
/// read-only turns, star-cliproxy route evidence and the full details drawer with honest "Not reported"
/// placeholders.
/// </summary>
public sealed class WorkflowActivityMonitorViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutARun_NoRoleSchemaOrRunFieldsAreInvented()
    {
        var viewModel = CreateViewModel();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer"));

        Assert.False(viewModel.HasRun);
        Assert.False(viewModel.HasNodes);
        Assert.Empty(viewModel.Nodes);
        Assert.Empty(viewModel.Activity);
        Assert.False(viewModel.IsDrawerOpen);
        Assert.Equal("Architect, Implementer", viewModel.DeclaredRolesDisplay);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, viewModel.RunSummaryDisplay);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, viewModel.CurrentRoleDisplay);
        Assert.Contains("No workflow run is selected", viewModel.EmptyStateMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_IsOrderedByDeclaredRolesAndThenByObservedRoles()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-120),
                Now.AddMinutes(-110),
                Now.AddMinutes(-110)),
            Projection(
                "exec-escalation",
                "Escalation",
                ExecutionState.Failed,
                Now.AddMinutes(-100),
                Now.AddMinutes(-95),
                Now.AddMinutes(-95))
        });

        // The declared roles of the active version lead the schema; the observed roles follow. The
        // chain order is a layout, and an observed role is never dropped.
        Assert.Equal(
            new[] { "Architect", "Implementer", "Reviewer", "Escalation" },
            viewModel.Nodes.Select(node => node.RoleId).ToArray());
        Assert.Equal("Architect, Implementer, Reviewer", viewModel.DeclaredRolesDisplay);
        Assert.Equal("→", viewModel.Nodes[0].TransitionArrowDisplay);
        Assert.Equal("→", viewModel.Nodes[2].TransitionArrowDisplay);
        Assert.Empty(viewModel.Nodes[3].TransitionArrowDisplay);
        Assert.True(viewModel.Nodes.Single(node => node.RoleId == "Reviewer").IsCurrent);
        Assert.False(viewModel.Nodes.Single(node => node.RoleId == "Architect").IsCurrent);
    }

    [Fact]
    public void NodeStatuses_MapToTheFiveRequiredStates()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion(
            "Coordinator",
            "Architect",
            "Implementer",
            "Reviewer",
            "UiReviewer",
            "Tester"));

        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-coordinator",
                "Coordinator",
                ExecutionState.Cancelled,
                Now.AddMinutes(-140),
                Now.AddMinutes(-135),
                Now.AddMinutes(-135)),
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-120),
                Now.AddMinutes(-110),
                Now.AddMinutes(-110)),
            Projection(
                "exec-implementer",
                "Implementer",
                ExecutionState.TimedOut,
                Now.AddMinutes(-90),
                Now.AddMinutes(-70),
                Now.AddMinutes(-70)),
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-5)),
            Projection(
                "exec-ui-reviewer",
                "UiReviewer",
                ExecutionState.Ambiguous,
                Now.AddMinutes(-25),
                Now.AddMinutes(-20),
                Now.AddMinutes(-20))
        });

        Assert.Equal(
            WorkflowActivityNodeStatus.Stopped,
            viewModel.Nodes.Single(node => node.RoleId == "Coordinator").Status);
        Assert.Equal(
            WorkflowActivityNodeStatus.Completed,
            viewModel.Nodes.Single(node => node.RoleId == "Architect").Status);
        Assert.Equal(
            WorkflowActivityNodeStatus.Stopped,
            viewModel.Nodes.Single(node => node.RoleId == "Implementer").Status);
        Assert.Equal(
            WorkflowActivityNodeStatus.Running,
            viewModel.Nodes.Single(node => node.RoleId == "Reviewer").Status);
        Assert.Equal(
            WorkflowActivityNodeStatus.Stalled,
            viewModel.Nodes.Single(node => node.RoleId == "UiReviewer").Status);
        Assert.Equal(
            WorkflowActivityNodeStatus.Waiting,
            viewModel.Nodes.Single(node => node.RoleId == "Tester").Status);

        Assert.Equal(
            new[] { "Остановлен", "Готово", "Остановлен", "Работает", "Завис", "Ждёт" },
            viewModel.Nodes.Select(node => node.StatusDisplay).ToArray());
        Assert.True(viewModel.Nodes.Single(node => node.RoleId == "Reviewer").IsWorking);
    }

    [Fact]
    public void ParallelReadOnlyTurns_AreMarkedOnEveryActuallyWorkingNode()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Implementer", "Reviewer", "UiReviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-implementer",
                "Implementer",
                ExecutionState.Succeeded,
                Now.AddMinutes(-90),
                Now.AddMinutes(-70),
                Now.AddMinutes(-70)),
            Projection(
                "exec-reviewer-1",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-10),
                observedExecutionMode: "review",
                isReadOnlyTurn: true),
            Projection(
                "exec-reviewer-2",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-28),
                Now.AddMinutes(-8),
                observedExecutionMode: "plan",
                isReadOnlyTurn: true),
            Projection(
                "exec-ui-reviewer",
                "UiReviewer",
                ExecutionState.Running,
                Now.AddMinutes(-25),
                Now.AddMinutes(-6),
                observedExecutionMode: "review",
                isReadOnlyTurn: true)
        });

        var reviewer = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");
        var uiReviewer = viewModel.Nodes.Single(node => node.RoleId == "UiReviewer");

        Assert.Equal(2, reviewer.ActiveTurnCount);
        Assert.Equal(2, reviewer.ReadOnlyActiveTurnCount);
        Assert.True(reviewer.IsParallelReadOnly);
        Assert.Equal("2 parallel read-only turns", reviewer.ParallelDisplay);
        Assert.Equal(1, uiReviewer.ActiveTurnCount);
        Assert.Equal(1, uiReviewer.ReadOnlyActiveTurnCount);
        Assert.False(uiReviewer.IsParallelReadOnly);
        Assert.True(viewModel.HasParallelReadOnlyActivity);
        Assert.Contains(
            "execution mode proves they do not take the writer lock",
            viewModel.ConcurrencyNote,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TwoWriterTurns_AreNeverReportedAsParallelReadOnly()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            // Two concurrent active turns, but both execution modes require the writer lock.
            Projection(
                "exec-reviewer-1",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-10),
                observedExecutionMode: "apply",
                isReadOnlyTurn: false),
            Projection(
                "exec-reviewer-2",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-28),
                Now.AddMinutes(-8),
                observedExecutionMode: null,
                isReadOnlyTurn: false)
        });

        var reviewer = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");

        Assert.Equal(2, reviewer.ActiveTurnCount);
        Assert.Equal(0, reviewer.ReadOnlyActiveTurnCount);
        Assert.False(reviewer.IsParallelReadOnly);
        Assert.Empty(reviewer.ParallelDisplay);
        Assert.False(viewModel.HasParallelReadOnlyActivity);

        // The working state itself still comes from the observed executions.
        Assert.Equal(WorkflowActivityNodeStatus.Running, reviewer.Status);
    }

    [Fact]
    public void TheCurrentRoleWithoutAnObservedExecutionIsNeverReportedAsWorking()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        // The run state is Running, but no execution of the current role was ever observed.
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal("Reviewer", run.CurrentRole);

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, Array.Empty<ObservableRunProjection>());

        var current = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");

        Assert.Equal(WorkflowActivityNodeStatus.Waiting, current.Status);
        Assert.Equal("Ждёт", current.StatusDisplay);
        Assert.False(current.IsWorking);
        Assert.Equal(0, current.ActiveTurnCount);
    }

    [Fact]
    public void ATerminalStalledTurn_IsStalledButNotWorking()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("UiReviewer"));

        // The only observed turn of the role ended in Ambiguous: the node is stalled, but nothing is
        // running any more, so it must not be presented as an actually working node.
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-ui-reviewer",
                "UiReviewer",
                ExecutionState.Ambiguous,
                Now.AddMinutes(-25),
                Now.AddMinutes(-20),
                Now.AddMinutes(-20))
        });

        var node = viewModel.Nodes.Single(candidate => candidate.RoleId == "UiReviewer");

        Assert.Equal(WorkflowActivityNodeStatus.Stalled, node.Status);
        Assert.Equal("Завис", node.StatusDisplay);
        Assert.Equal(0, node.ActiveTurnCount);
        Assert.False(node.HasActiveTurns);
        Assert.False(node.IsWorking);
    }

    [Fact]
    public void ModelAndAccount_AreShownOnlyWhenTheObservedSessionBindingSuppliesThem()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Reviewer", "UiReviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-10),
                observedModelId: "model-observed",
                observedAccountId: "account-observed",
                observedExecutionMode: "review",
                isReadOnlyTurn: true),
            // The session binding reported no model and no account for this turn.
            Projection(
                "exec-ui-reviewer",
                "UiReviewer",
                ExecutionState.Running,
                Now.AddMinutes(-20),
                Now.AddMinutes(-5))
        });

        var reviewer = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");
        var uiReviewer = viewModel.Nodes.Single(node => node.RoleId == "UiReviewer");

        Assert.Equal("model-observed", reviewer.ModelSummaryDisplay);
        Assert.Equal("account-observed", reviewer.AccountSummaryDisplay);
        Assert.Equal("Model: model-observed · Account: account-observed", reviewer.ModelAccountDisplay);

        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            uiReviewer.ModelSummaryDisplay);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            uiReviewer.AccountSummaryDisplay);

        var reviewerEvidence = reviewer.RouteEvidence.Single();
        var uiReviewerEvidence = uiReviewer.RouteEvidence.Single();

        Assert.Equal("model-observed", reviewerEvidence.ModelDisplay);
        Assert.Equal("account-observed", reviewerEvidence.AccountDisplay);
        Assert.Equal("review", reviewerEvidence.ExecutionModeDisplay);
        Assert.True(reviewerEvidence.IsReadOnlyTurn);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            uiReviewerEvidence.ModelDisplay);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            uiReviewerEvidence.AccountDisplay);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            uiReviewerEvidence.ExecutionModeDisplay);
        Assert.False(uiReviewerEvidence.IsReadOnlyTurn);
    }

    [Fact]
    public void CodexAndAgy_AreShownAsObservedStarCliProxyRoutesOfTheAssignedRole()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-120),
                Now.AddMinutes(-110),
                Now.AddMinutes(-110),
                requestedRouteId: "route-star-cliproxy-codex",
                observedRouteId: "route-star-cliproxy-codex",
                nativeSessionId: "native-codex"),
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-5),
                requestedRouteId: "route-star-cliproxy-agy",
                observedRouteId: "route-star-cliproxy-agy",
                nativeSessionId: "native-agy")
        });

        var architect = viewModel.Nodes.Single(node => node.RoleId == "Architect");
        var reviewer = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");

        // The role is the node identity; Codex and AGY appear only as proven route evidence.
        Assert.Equal("Architect", architect.DisplayName);
        Assert.Equal("Reviewer", reviewer.DisplayName);
        Assert.DoesNotContain("Codex", architect.DisplayName, StringComparison.Ordinal);
        Assert.Equal(
            "Codex via star-cliproxy (route-star-cliproxy-codex)",
            architect.RouteSummaryDisplay);
        Assert.Equal(
            "AGY via star-cliproxy (route-star-cliproxy-agy)",
            reviewer.RouteSummaryDisplay);

        var evidence = Assert.Single(reviewer.RouteEvidence);
        Assert.Equal(
            "Observed: AGY via star-cliproxy (route-star-cliproxy-agy) · "
            + "Requested: AGY via star-cliproxy (route-star-cliproxy-agy)",
            evidence.RouteEvidenceDisplay);
        Assert.Equal("native-agy", evidence.NativeSessionDisplay);
        Assert.Contains("star-cliproxy", viewModel.RouteProvenanceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawer_ShowsTheSourceRoleAndTheReceivedAndUpdatedTimestamps()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-120),
                Now.AddMinutes(-110),
                Now.AddMinutes(-110)),
            Projection(
                "exec-implementer",
                "Implementer",
                ExecutionState.Succeeded,
                Now.AddMinutes(-100),
                Now.AddMinutes(-80),
                Now.AddMinutes(-80)),
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-5))
        });

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");

        Assert.True(viewModel.IsDrawerOpen);
        Assert.True(viewModel.Drawer.IsOpen);
        Assert.Equal("← от Implementer", viewModel.Drawer.SourceRoleDisplay);
        Assert.Equal(
            $"Получено: {Format(Now.AddMinutes(-30))}",
            viewModel.Drawer.ReceivedAtDisplay);
        Assert.Equal(
            $"Изменено: {Format(Now.AddMinutes(-5))}",
            viewModel.Drawer.UpdatedAtDisplay);
        Assert.Equal("1 active turn(s)", viewModel.Drawer.ActiveTurnsDisplay);
        Assert.Equal("Observable run projection", viewModel.Drawer.EvidenceSourceDisplay);
    }

    [Fact]
    public void Drawer_ShowsTheFullUntruncatedWorkText()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();
        var longWorkText = string.Join(
            " ",
            Enumerable.Repeat("Полный текст текущей работы передаётся без обрезания.", 60));

        Assert.True(longWorkText.Length > 1500, "The test text must be long enough to prove no truncation.");

        run.RecordReviewerVerdict(new ReviewerVerdictRecord(
            "Reviewer",
            "route-star-cliproxy-agy",
            Hash('d'),
            WorkflowReviewVerdict.RequestChanges,
            longWorkText,
            Now.AddMinutes(-6),
            "execution-reviewer",
            "stage-review",
            "artifact-review"));

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-5))
        });

        viewModel.SelectedNode = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");

        Assert.True(viewModel.Drawer.HasWorkText);
        Assert.Contains(longWorkText, viewModel.Drawer.WorkText, StringComparison.Ordinal);
        Assert.True(viewModel.Drawer.WorkText.Length >= longWorkText.Length);
        Assert.Equal(viewModel.Drawer.WorkText, viewModel.Drawer.WorkTextDisplay);
        Assert.DoesNotContain("…", viewModel.Drawer.WorkText, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawer_ActivityIsFilteredToTheConfiguredWindow()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Implementer", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-110),
                Now.AddMinutes(-100),
                Now.AddMinutes(-100)),
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-20),
                Now.AddMinutes(-10))
        });

        // Default window: 60 minutes. The run started 2 hours ago, so every transition and the architect
        // execution are outside the window; only the reviewer turn is reported as activity.
        Assert.Equal(WorkflowActivityMonitorViewModel.DefaultHistoryWindowMinutes, viewModel.HistoryWindowMinutes);
        Assert.Equal("Last 60 min", viewModel.HistoryWindowDisplay);
        var observedInDefaultWindow = Assert.Single(viewModel.Activity);
        Assert.Equal(Format(Now.AddMinutes(-10)), observedInDefaultWindow.OccurredAtDisplay);

        viewModel.HistoryWindowMinutes = 120;

        Assert.Equal(4, viewModel.Activity.Count);
        Assert.Contains(
            viewModel.Activity,
            item => item.OccurredAtDisplay == Format(Now.AddMinutes(-100)));

        viewModel.HistoryWindowMinutes = 15;

        Assert.Single(viewModel.Activity);
        Assert.Equal(Format(Now.AddMinutes(-10)), viewModel.Activity[0].OccurredAtDisplay);
        Assert.Contains("15 minutes", viewModel.ActivityWindowNote, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFields_AreReportedAsNotReported()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Architect", "Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                startedAt: null,
                lastActivityAt: Now.AddMinutes(-110),
                endedAt: null,
                observedRouteId: null,
                nativeSessionId: null),
            Projection(
                "exec-reviewer",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-5))
        });

        var architect = viewModel.Nodes.Single(node => node.RoleId == "Architect");

        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, architect.RouteSummaryDisplay);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            Assert.Single(architect.RouteEvidence).ObservedRouteDisplay);

        viewModel.SelectedNode = architect;

        // The first observed role has no reported predecessor, no received timestamp and no work text.
        Assert.Equal("← от Not reported", viewModel.Drawer.SourceRoleDisplay);
        Assert.Equal(
            $"Получено: {WorkflowActivityMonitorViewModel.NotReportedPlaceholder}",
            viewModel.Drawer.ReceivedAtDisplay);
        Assert.False(viewModel.Drawer.HasWorkText);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, viewModel.Drawer.WorkTextDisplay);
        Assert.Equal(string.Empty, viewModel.Drawer.WorkText);
        Assert.Equal(
            WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
            Assert.Single(architect.RouteEvidence).NativeSessionDisplay);
    }

    [Fact]
    public void TerminalRuns_MapTheCurrentRoleToCompletedOrStopped()
    {
        var completedRun = CreateRunWithTransitions();
        completedRun.Complete("The run reached its terminal outcome.", Now.AddMinutes(40));

        var completedViewModel = CreateViewModel();
        completedViewModel.LoadActiveVersion(CreateVersion("Reviewer"));
        completedViewModel.LoadRun(completedRun);

        Assert.True(completedViewModel.IsRunTerminal);
        Assert.Equal(
            WorkflowActivityNodeStatus.Completed,
            completedViewModel.Nodes.Single(node => node.RoleId == "Reviewer").Status);
        Assert.Equal("Готово", completedViewModel.Nodes.Single(node => node.RoleId == "Reviewer").StatusDisplay);

        var cancelledRun = CreateRunWithTransitions();
        cancelledRun.Cancel("The operator cancelled the run.", Now.AddMinutes(35));

        var cancelledViewModel = CreateViewModel();
        cancelledViewModel.LoadActiveVersion(CreateVersion("Reviewer"));
        cancelledViewModel.LoadRun(cancelledRun);

        Assert.Equal(
            WorkflowActivityNodeStatus.Stopped,
            cancelledViewModel.Nodes.Single(node => node.RoleId == "Reviewer").Status);
        Assert.Equal("Остановлен", cancelledViewModel.Nodes.Single(node => node.RoleId == "Reviewer").StatusDisplay);
    }

    [Fact]
    public void ClearingTheRun_ClosesTheDrawerAndReportsTheEmptyState()
    {
        var viewModel = CreateViewModel();
        var run = CreateRunWithTransitions();

        viewModel.LoadActiveVersion(CreateVersion("Reviewer"));
        viewModel.LoadRun(run, new[]
        {
            Projection("exec-reviewer", "Reviewer", ExecutionState.Running, Now.AddMinutes(-10), Now.AddMinutes(-2))
        });

        viewModel.SelectedNode = viewModel.Nodes.Single();

        Assert.True(viewModel.IsDrawerOpen);

        viewModel.ClearRun();

        Assert.False(viewModel.HasRun);
        Assert.Empty(viewModel.Nodes);
        Assert.False(viewModel.IsDrawerOpen);
        Assert.Empty(viewModel.TransferEvidence);
        Assert.Contains("No workflow run is selected", viewModel.EmptyStateMessage, StringComparison.Ordinal);
    }

    private static WorkflowActivityMonitorViewModel CreateViewModel() =>
        new(
            new WorkflowRunTimelineService(),
            new RoleTransferEvidenceProjector(),
            new ActivityMonitorTimeProvider(Now));

    private static WorkflowVersion CreateVersion(params string[] roles) =>
        new(
            "ver-1",
            "pkg-1",
            1,
            Hash('a'),
            Hash('b'),
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: JsonSerializer.Serialize(roles),
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            Now.AddDays(-1),
            activatedAtUtc: null);

    private static WorkflowRun CreateRunWithTransitions()
    {
        var startedAt = Now.AddHours(-2);
        var architecture = CreateStage("stage-architecture", "Architect", "stage-implementation");
        var implementation = CreateStage("stage-implementation", "Implementer", "stage-review");
        var review = CreateStage("stage-review", "Reviewer", nextStageId: null);

        var run = WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            architecture,
            startedAt);

        run.AdvanceTo(
            architecture,
            implementation,
            "Architecture document delivered.",
            startedAt.AddMinutes(10));
        run.AdvanceTo(
            implementation,
            review,
            "Implementation diff delivered for review.",
            startedAt.AddMinutes(20));

        return run;
    }

    private static WorkflowStageDefinition CreateStage(string stageId, string role, string? nextStageId) =>
        new(
            stageId,
            $"Display {stageId}",
            role,
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId,
            failureStageId: null);

    private static ObservableRunProjection Projection(
        string executionId,
        string roleLabel,
        ExecutionState state,
        DateTimeOffset? startedAt,
        DateTimeOffset lastActivityAt,
        DateTimeOffset? endedAt = null,
        string? requestedRouteId = null,
        string? observedRouteId = null,
        string? nativeSessionId = null,
        string? observedModelId = null,
        string? observedAccountId = null,
        string? observedExecutionMode = null,
        bool isReadOnlyTurn = false)
    {
        return new ObservableRunProjection(
            executionId,
            "session-1",
            WorkflowRoleParser.Parse(roleLabel),
            roleLabel,
            state,
            requestedRouteId ?? "route-opencode",
            observedRouteId,
            nativeSessionId,
            startedAt,
            lastActivityAt,
            endedAt,
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false,
            observedModelId,
            observedAccountId,
            observedExecutionMode,
            isReadOnlyTurn);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("u", CultureInfo.InvariantCulture);

    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private sealed class ActivityMonitorTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public ActivityMonitorTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
