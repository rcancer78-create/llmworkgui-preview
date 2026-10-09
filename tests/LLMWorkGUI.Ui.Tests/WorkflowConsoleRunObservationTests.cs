using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition-root and observation evidence for the shipped Workflow Console. A green view-model test
/// is meaningless if the product registration never hands the screen the registered workflow services or
/// if the screen never reads an actual run, so the production <c>AddAppUi</c> registration, the observed run
/// of the selected project and the observed model/account/read-only proof are asserted here.
///
/// Every run field shown by the console is required to come from stored evidence: a run of another
/// workflow version, a run of another project or a run without stored executions must be reported as
/// "Not reported" instead of being filled in (РўР— В§7.2, В§7.4; ROADMAP 10D, 10E).
/// </summary>
public sealed class WorkflowConsoleRunObservationTests
{
    private const string ProjectA = "project-a";
    private const string ProjectB = "project-b";
    private const string PackageId = "pkg-1";
    private const string VersionA = "ver-a";
    private const string VersionB = "ver-b";

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultipleActiveRuns_ObserveNewestRunAsTheProductionRepositoryDoes()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, 1, new[] { "Reviewer" });
        var stage = new WorkflowStageDefinition("review", "Review", "Reviewer", WorkflowStageKind.Custom,
            Array.Empty<string>(), false, null, null, null);
        var older = WorkflowRun.Start("older", ProjectA, PackageId, VersionA, null, stage, Now.AddMinutes(-2));
        var newer = WorkflowRun.Start("newer", ProjectA, PackageId, VersionA, null, stage, Now.AddMinutes(-1));
        harness.Runs.Runs[older.Id] = older;
        harness.Runs.Runs[newer.Id] = newer;
        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();
        Assert.Same(newer, library.ObservedRun);
    }

    [Fact]
    public async Task ObservationStorageFailure_DoesNotPublishRawPrivateDiagnostics()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, 1, new[] { "Reviewer" });
        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        harness.Runs.ReadFailure = new InvalidOperationException("token=synthetic-private-observation");
        await library.ObserveActiveRunAsync();
        Assert.True(library.HasBlocker);
        Assert.False(library.HasObservedRun);
        Assert.DoesNotContain("synthetic-private-observation", library.Blocker);
    }

    [Fact]
    public void AddAppUi_ForwardsTheRegisteredWorkflowServicesIntoTheLibraryAndItsPanels()
    {
        using var provider = CreateProviderWithObservedWorkflowServices();

        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();

        // The screen must project with the services the composition registered, not with privately
        // constructed fallbacks: the very same instances have to reach both embedded panels.
        Assert.Same(provider.GetRequiredService<IWorkflowRunTimelineService>(), library.TimelineService);
        Assert.Same(library.TimelineService, library.ActivityMonitor.TimelineService);
        Assert.Same(provider.GetRequiredService<IWorkflowStudioService>(), library.Studio.StudioService);
        Assert.Same(
            provider.GetRequiredService<IDocumentTemplateService>(),
            library.Studio.DocumentTemplateService);
        Assert.Same(provider.GetRequiredService<IPreCoderGateValidator>(), library.Studio.GateValidator);

        Assert.Same(provider.GetRequiredService<IWorkflowRunRepository>(), library.RunRepository);
        Assert.Same(provider.GetRequiredService<ISessionRepository>(), library.SessionRepository);
        Assert.Same(provider.GetRequiredService<IExecutionRepository>(), library.ExecutionRepository);
        Assert.Same(provider.GetRequiredService<ICheckoutLockService>(), library.CheckoutLockService);

        Assert.True(library.IsStudioAvailable);
        Assert.True(library.IsRunObservationAvailable);
    }

    [Fact]
    public void AddAppUi_WithoutTheWorkflowServices_ReportsTheConsoleUnavailableAndNeverInventsARun()
    {
        using var provider = UiTestHost.CreateProvider();

        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();

        Assert.False(library.IsStudioAvailable);
        Assert.False(library.IsRunObservationAvailable);
        Assert.False(library.HasObservedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedProjectionsDisplay);
        Assert.Contains("Not reported", library.ObservedRunNotice, StringComparison.Ordinal);
        Assert.NotEmpty(library.Studio.UnavailableNotice);
        Assert.False(library.ActivityMonitor.HasRun);
    }

    [Fact]
    public async Task AMatchingActiveRun_LoadsItsStoredExecutionsIntoTheMonitorAndTheStudio()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();

        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        Assert.True(library.HasObservedRun);
        Assert.Same(run, library.ObservedRun);
        Assert.Equal("run-a", library.ObservedRunDisplay.Split(' ')[0]);

        // The projections come from the stored executions of the run's own session, not from the run
        // aggregate and not from the workflow timeline.
        var projection = Assert.Single(library.ObservedProjections);
        Assert.Equal("exec-reviewer", projection.ExecutionId);
        Assert.Equal("session-reviewer", projection.SessionId);
        Assert.Equal("model-observed", projection.ObservedModelId);
        Assert.Equal("account-observed", projection.ObservedAccountId);
        Assert.Equal("review", projection.ObservedExecutionMode);
        Assert.True(projection.IsActive);

        Assert.True(library.ActivityMonitor.HasRun);
        var node = Assert.Single(library.ActivityMonitor.Nodes);
        Assert.Equal("Reviewer", node.RoleId);
        Assert.Equal(WorkflowActivityNodeStatus.Running, node.Status);
        Assert.True(node.IsWorking);
        Assert.Equal("model-observed", node.ModelSummaryDisplay);
        Assert.Equal("account-observed", node.AccountSummaryDisplay);

        // The Studio shows the observed run graph instead of only the editable template schema.
        Assert.True(library.Studio.HasRunGraph);
        Assert.Contains("run-a", library.Studio.RunGraphSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APinnedRunIsObservedForItsSourceVersionAndReportsItsTemplateSeparately()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionB, versionNumber: 2, roles: new[] { "Implementer" });

        var run = harness.AddPinnedRun(ProjectA, VersionB, PackageId, "run-pinned", "linear-template", 2);
        harness.AddSession(
            "session-pinned",
            ProjectA,
            run.Id,
            "Implementer",
            "model-observed",
            "account-observed",
            "implement");

        var library = harness.CreateLibrary();

        await library.RefreshAsync();
        library.SelectedProject = library.Projects.Single(project => project.Id == ProjectA);
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionB);
        await library.ObserveActiveRunAsync();

        // The run is still matched on its source workflow version, which pinning does not replace.
        Assert.True(library.HasObservedRun);
        Assert.Same(run, library.ObservedRun);
        Assert.NotNull(library.ObservedRun);
        Assert.Equal(VersionB, library.ObservedRun!.WorkflowVersionId);
        Assert.Contains("run-pinned", library.ObservedRunDisplay, StringComparison.Ordinal);

        // The assigned template version is visible as its own field, read from the run and not from the
        // project, the package or the version that the screen happens to have selected.
        Assert.Equal("linear-template@2", library.ObservedRunTemplateDisplay);
    }

    [Fact]
    public async Task ALegacyRunReportsNoTemplateIdentityInsteadOfTheSourceVersion()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionB, versionNumber: 2, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionB, PackageId, "run-legacy-run", "session-legacy");
        harness.AddSession(
            "session-legacy",
            ProjectA,
            run.Id,
            "Reviewer",
            "model-observed",
            "account-observed",
            "review");

        var library = harness.CreateLibrary();

        await library.RefreshAsync();
        library.SelectedProject = library.Projects.Single(project => project.Id == ProjectA);
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionB);
        await library.ObserveActiveRunAsync();

        Assert.True(library.HasObservedRun);
        Assert.NotNull(library.ObservedRun);
        Assert.Equal(VersionB, library.ObservedRun!.WorkflowVersionId);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunTemplateDisplay);
    }

    [Fact]
    public async Task WithoutAnActiveRun_TheObservedStateIsClearedAndReportedAsNotReported()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();

        Assert.True(library.ActivityMonitor.HasRun);

        harness.Runs.Runs.Remove(run.Id);
        await library.ObserveActiveRunAsync();

        Assert.False(library.HasObservedRun);
        Assert.Empty(library.ObservedProjections);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedProjectionsDisplay);
        Assert.False(library.ActivityMonitor.HasRun);
        Assert.Empty(library.ActivityMonitor.Nodes);
        Assert.False(library.Studio.HasRunGraph);
        Assert.Equal(WorkflowStudioViewModel.NoRunGraphMessage, library.Studio.RunGraphSummary);
    }

    [Fact]
    public async Task ARunPinnedToAnotherWorkflowVersion_IsNeverShownForTheSelectedVersion()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });
        harness.AddVersion(PackageId, VersionB, versionNumber: 2, roles: new[] { "Reviewer" });

        // The active run belongs to version B while version A is the selected version.
        var run = harness.AddRun(ProjectA, VersionB, PackageId, "run-b", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();

        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionA);
        await library.ObserveActiveRunAsync();

        Assert.False(library.HasObservedRun);
        Assert.Empty(library.ObservedProjections);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.False(library.ActivityMonitor.HasRun);
        Assert.False(library.Studio.HasRunGraph);

        // Selecting the version the run is actually pinned to shows the run again.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionB);
        await library.ObserveActiveRunAsync();

        Assert.True(library.HasObservedRun);
        Assert.Equal("run-b", library.ObservedRun!.Id);
        Assert.Equal("Reviewer", Assert.Single(library.ActivityMonitor.Nodes).RoleId);
    }

    [Fact]
    public async Task RefreshAndProjectReturn_SelectTheActiveVersionAndKeepItsRunObserved()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddProject(ProjectB);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });
        harness.AddVersion(PackageId, VersionB, versionNumber: 2, roles: new[] { "Reviewer" });

        // The project has two versions with v2 active, and the live run of the project is pinned to v2.
        harness.AddBinding(ProjectA, PackageId, VersionB);
        harness.AddBinding(ProjectB, PackageId, VersionA);

        var run = harness.AddRun(ProjectA, VersionB, PackageId, "run-a", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        // The repository returns the oldest version first, so a refresh that simply took the first entry
        // would leave the observed run of the active v2 unreported behind the selected v1.
        Assert.Equal(VersionA, library.Versions[0].Id);
        Assert.Equal(VersionB, library.SelectedVersion!.Id);
        Assert.True(library.SelectedVersion.IsActiveInCurrentProject);
        Assert.True(library.HasObservedRun);
        Assert.Equal(run.Id, library.ObservedRun!.Id);
        Assert.Equal("model-observed", Assert.Single(library.ObservedProjections).ObservedModelId);
        Assert.True(library.ActivityMonitor.HasRun);
        Assert.Equal("Reviewer", Assert.Single(library.ActivityMonitor.Nodes).RoleId);
        Assert.True(library.Studio.HasRunGraph);

        // The other project has its own active version and no run, so nothing may leak across projects.
        library.SelectedProject = library.Projects.Single(project => project.Id == ProjectB);
        await library.ObserveActiveRunAsync();

        Assert.Equal(VersionA, library.SelectedVersion!.Id);
        Assert.True(library.SelectedVersion.IsActiveInCurrentProject);
        Assert.False(library.HasObservedRun);
        Assert.Empty(library.ObservedProjections);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.False(library.ActivityMonitor.HasRun);
        Assert.False(library.Studio.HasRunGraph);

        // Returning to the first project restores its active version together with the observed run.
        library.SelectedProject = library.Projects.Single(project => project.Id == ProjectA);
        await library.ObserveActiveRunAsync();

        Assert.Equal(VersionB, library.SelectedVersion!.Id);
        Assert.True(library.SelectedVersion.IsActiveInCurrentProject);
        Assert.True(library.HasObservedRun);
        Assert.Equal(run.Id, library.ObservedRun!.Id);
        Assert.Equal("model-observed", Assert.Single(library.ObservedProjections).ObservedModelId);
        Assert.True(library.ActivityMonitor.HasRun);
        Assert.True(library.Studio.HasRunGraph);

        // A deliberate selection of the other version is not overridden and stays fail-closed.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionA);
        await library.ObserveActiveRunAsync();

        Assert.Equal(VersionA, library.SelectedVersion!.Id);
        Assert.False(library.HasObservedRun);
        Assert.Empty(library.ObservedProjections);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.False(library.ActivityMonitor.HasRun);
        Assert.False(library.Studio.HasRunGraph);
    }

    [Fact]
    public async Task SwitchingProjects_ShowsOnlyTheRunOfTheSelectedProject()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddProject(ProjectB);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var runA = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-a");
        harness.AddSession("session-a", ProjectA, runA.Id, "Reviewer", "model-a", "account-a", "review");
        harness.AddExecution("exec-a", "session-a", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var runB = harness.AddRun(ProjectB, VersionA, PackageId, "run-b", "session-b");
        harness.AddSession("session-b", ProjectB, runB.Id, "Architect", "model-b", "account-b", "apply");
        harness.AddExecution("exec-b", "session-b", ExecutionState.Running, startedAt: Now.AddMinutes(-20));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        Assert.Equal("run-a", library.ObservedRun!.Id);
        Assert.Equal("model-a", Assert.Single(library.ObservedProjections).ObservedModelId);

        library.SelectedProject = library.Projects.Single(project => project.Id == ProjectB);
        await library.ObserveActiveRunAsync();

        Assert.Equal("run-b", library.ObservedRun!.Id);
        var projection = Assert.Single(library.ObservedProjections);
        Assert.Equal("session-b", projection.SessionId);
        Assert.Equal("model-b", projection.ObservedModelId);

        // The declared "Reviewer" role of the version survives the project switch as a waiting node; only
        // the run of the selected project contributes an observed turn.
        Assert.Equal(2, library.ActivityMonitor.Nodes.Count);
        var architect = library.ActivityMonitor.Nodes.Single(node => node.RoleId == "Architect");
        Assert.Equal("account-b", architect.AccountSummaryDisplay);
        Assert.Equal(1, architect.ActiveTurnCount);
        Assert.Equal(0, architect.ReadOnlyActiveTurnCount);
        Assert.False(architect.IsParallelReadOnly);
        Assert.Equal(WorkflowActivityNodeStatus.Running, architect.Status);

        var reviewer = library.ActivityMonitor.Nodes.Single(node => node.RoleId == "Reviewer");
        Assert.Equal(0, reviewer.ActiveTurnCount);
        Assert.Equal(WorkflowActivityNodeStatus.Waiting, reviewer.Status);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, reviewer.ModelSummaryDisplay);
    }

    [Fact]
    public async Task TwoProvenReadOnlyTurns_AreParallelAndTwoWriterTurnsNeverAre()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-readonly-1");
        harness.AddSession("session-readonly-1", ProjectA, run.Id, "Reviewer", "model-1", "account-1", "review");
        harness.AddExecution("exec-readonly-1", "session-readonly-1", ExecutionState.Running, startedAt: Now.AddMinutes(-10));
        harness.AddSession("session-readonly-2", ProjectA, run.Id, "Reviewer", "model-2", "account-2", "plan");
        harness.AddExecution("exec-readonly-2", "session-readonly-2", ExecutionState.Running, startedAt: Now.AddMinutes(-8));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        var readOnlyNode = Assert.Single(library.ActivityMonitor.Nodes);
        Assert.Equal(2, readOnlyNode.ActiveTurnCount);
        Assert.Equal(2, readOnlyNode.ReadOnlyActiveTurnCount);
        Assert.True(readOnlyNode.IsParallelReadOnly);
        Assert.Equal("2 parallel read-only turns", readOnlyNode.ParallelDisplay);
        Assert.True(library.ActivityMonitor.HasParallelReadOnlyActivity);

        // The same two concurrent turns in a mode that requires the writer lock are never reported as
        // parallel read-only activity, and a null execution mode is a writer turn.
        harness.ReplaceRunSessionMode("session-readonly-1", "apply");
        harness.ReplaceRunSessionMode("session-readonly-2", executionMode: null);
        await library.ObserveActiveRunAsync();

        var writerNode = Assert.Single(library.ActivityMonitor.Nodes);
        Assert.Equal(2, writerNode.ActiveTurnCount);
        Assert.Equal(0, writerNode.ReadOnlyActiveTurnCount);
        Assert.False(writerNode.IsParallelReadOnly);
        Assert.Empty(writerNode.ParallelDisplay);
        Assert.False(library.ActivityMonitor.HasParallelReadOnlyActivity);
    }

    [Fact]
    public async Task ModelAndAccount_AppearOnlyWhenTheObservedSessionBindingSuppliesThem()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer", "Architect" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-bound");
        harness.AddSession("session-bound", ProjectA, run.Id, "Reviewer", "model-bound", "account-bound", "review");
        harness.AddExecution("exec-bound", "session-bound", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        var bound = library.ActivityMonitor.Nodes.Single(node => node.RoleId == "Reviewer");
        Assert.Equal("model-bound", bound.ModelSummaryDisplay);
        Assert.Equal("account-bound", bound.AccountSummaryDisplay);

        // A declared role without an observed session has no model, no account and no turn at all, so it
        // stays "Not reported" instead of borrowing the evidence of the role that has a session.
        var unbound = library.ActivityMonitor.Nodes.Single(node => node.RoleId == "Architect");
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, unbound.ModelSummaryDisplay);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, unbound.AccountSummaryDisplay);
        Assert.Equal(0, unbound.ActiveTurnCount);
        Assert.False(unbound.IsWorking);
        Assert.Equal(WorkflowActivityNodeStatus.Waiting, unbound.Status);
    }

    [Fact]
    public async Task ARunWithoutStoredExecutions_ReportsTheRunButNoProjections()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        // A run record with no stored execution proves no turn, no model, no account and no read-only mode.
        harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-missing");

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        Assert.True(library.HasObservedRun);
        Assert.Empty(library.ObservedProjections);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedProjectionsDisplay);

        var node = Assert.Single(library.ActivityMonitor.Nodes);
        Assert.Equal(WorkflowActivityNodeStatus.Waiting, node.Status);
        Assert.False(node.IsWorking);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, node.ModelSummaryDisplay);
        Assert.Equal(WorkflowActivityMonitorViewModel.NotReportedPlaceholder, node.AccountSummaryDisplay);
    }

    [Fact]
    public async Task TheConsoleHeader_FollowsTheRunObservedByTheLibrary()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        var console = new WorkflowConsolidatedViewModel(library);

        Assert.Equal(WorkflowConsolidatedViewModel.NotReportedPlaceholder, console.RunSummaryDisplay);

        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        Assert.Contains("run-a", console.RunSummaryDisplay, StringComparison.Ordinal);
        Assert.Contains(VersionA, console.SelectedWorkflowDisplay, StringComparison.Ordinal);

        harness.Runs.Runs.Remove(run.Id);
        await library.ObserveActiveRunAsync();

        // The header follows the cleared observation instead of keeping a stale run summary.
        Assert.Equal(WorkflowConsolidatedViewModel.NotReportedPlaceholder, console.RunSummaryDisplay);
    }

    [Fact]
    public async Task ObservingARun_OnlyReadsTheActiveRunAndNeverStartsAdvancesOrPolls()
    {
        var harness = new RunObservationHarness();
        harness.AddProject(ProjectA);
        harness.AddPackage(PackageId);
        harness.AddVersion(PackageId, VersionA, versionNumber: 1, roles: new[] { "Reviewer" });

        var run = harness.AddRun(ProjectA, VersionA, PackageId, "run-a", "session-reviewer");
        harness.AddSession("session-reviewer", ProjectA, run.Id, "Reviewer", "model-observed", "account-observed", "review");
        harness.AddExecution("exec-reviewer", "session-reviewer", ExecutionState.Running, startedAt: Now.AddMinutes(-10));

        var library = harness.CreateLibrary();
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        // Only the active run of the selected project is queried, and nothing is ever written back: the
        // console observes a run, it never starts, advances or re-runs one.
        Assert.NotEmpty(harness.Runs.ActiveQueries);
        Assert.All(harness.Runs.ActiveQueries, projectId => Assert.Equal(ProjectA, projectId));
        Assert.Equal(0, harness.Runs.Writes);
        Assert.Equal(0, harness.Sessions.Writes);
        Assert.Equal(0, harness.Executions.Writes);

        // Executions are read only for the sessions that actually belong to the observed run; no session of
        // the project outside the run is ever projected, and nothing is queried on a timer.
        Assert.NotEmpty(harness.Executions.SessionQueries);
        Assert.All(
            harness.Executions.SessionQueries,
            sessionId => Assert.Equal("session-reviewer", sessionId));

        // The observed run aggregate is untouched: still at the review stage with one transition.
        Assert.Equal("stage-review", run.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Single(run.Transitions);
    }

    [Fact]
    public void TheRealProductionComposition_HandsTheRegisteredWorkflowServicesToTheWorkflowConsole()
    {
        var appData = System.IO.Directory.CreateTempSubdirectory("llmworkgui-phase10-console-").FullName;

        try
        {
            // The shipped composition graph, exactly as App.OnStartup builds it, resolved without starting
            // any hosted service and without a migrated database: the console must still receive the
            // registered workflow singletons.
            using var host = HostBootstrapper
                .CreateHostBuilder(appDataDirectory: appData)
                .ConfigureServices((_, services) =>
                {
                    services.AddAppUi();
                    services.AddUnifiedWorkspaceShell();
                })
                .Build();

            var services = host.Services;
            var library = services.GetRequiredService<WorkflowLibraryViewModel>();

            Assert.True(library.IsLibraryAvailable);
            Assert.True(library.IsStudioAvailable);
            Assert.True(library.IsRunObservationAvailable);

            Assert.Same(services.GetRequiredService<IWorkflowRunTimelineService>(), library.TimelineService);
            Assert.Same(library.TimelineService, library.ActivityMonitor.TimelineService);
            Assert.Same(services.GetRequiredService<IWorkflowStudioService>(), library.Studio.StudioService);
            Assert.Same(services.GetRequiredService<IDocumentTemplateService>(), library.Studio.DocumentTemplateService);
            Assert.Same(services.GetRequiredService<IPreCoderGateValidator>(), library.Studio.GateValidator);
            Assert.Same(services.GetRequiredService<IWorkflowRunRepository>(), library.RunRepository);
            Assert.Same(services.GetRequiredService<ISessionRepository>(), library.SessionRepository);
            Assert.Same(services.GetRequiredService<IExecutionRepository>(), library.ExecutionRepository);
            Assert.Same(services.GetRequiredService<ICheckoutLockService>(), library.CheckoutLockService);

            // Without a project and a selected version nothing is queried and nothing is invented.
            Assert.False(library.HasObservedRun);
            Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
            Assert.Equal(WorkflowConsolidatedViewModel.NotReportedPlaceholder, services
                .GetRequiredService<WorkflowConsolidatedViewModel>()
                .RunSummaryDisplay);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(appData, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // A leftover temp directory must never turn a composition assertion red.
            }
        }
    }

    private static ServiceProvider CreateProviderWithObservedWorkflowServices()
    {
        var services = new ServiceCollection();
        var time = new FixedTimeProvider(Now);

        services.AddApplication();
        services.AddSingleton<IApplicationSettingsRepository>(new InMemoryApplicationSettingsRepository());
        services.AddSingleton<IAccountRepository>(new InMemoryAccountRepository());
        services.AddSingleton<IQuotaSnapshotRepository>(new InMemoryQuotaSnapshotRepository());
        services.AddSingleton<IThemeResourceApplier>(new RecordingThemeResourceApplier());
        services.AddSingleton<ISystemThemeProvider>(new FakeSystemThemeProvider());
        services.AddSingleton<ICliDetectionService>(FakeCliDetectionService.Degraded());
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new StorageOptions
        {
            AppDataDirectory = null,
            DatabaseFileName = StorageOptions.DefaultDatabaseFileName
        });

        var packages = new InMemoryWorkflowPackageRepository();
        var versions = new InMemoryWorkflowVersionRepository();
        var bindings = new InMemoryWorkflowBindingRepository();

        services.AddSingleton<IWorkflowPackageRepository>(packages);
        services.AddSingleton<IWorkflowVersionRepository>(versions);
        services.AddSingleton<IWorkflowBindingRepository>(bindings);
        services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        services.AddSingleton<IWorkflowPreviewService>(new StubWorkflowPreviewService());
        services.AddSingleton<ICheckoutLockService>(new CheckoutLockService(
            new RunObservationProjectLockRepository(),
            new RunObservationInstanceGuard(),
            time));

        // The Phase 10 workflow services are registered exactly as the production workflow composition
        // registers them, so the shipped screen can be asserted against the real singletons.
        services.AddSingleton<IWorkflowRunRepository>(new StubWorkflowRunStore());
        services.AddSingleton<ISessionRepository>(new RunObservationSessionStore());
        services.AddSingleton<IExecutionRepository>(new RunObservationExecutionStore());
        services.AddSingleton<IWorkflowRunTimelineService>(new WorkflowRunTimelineService());
        services.AddSingleton<RoleTransferEvidenceProjector>(new RoleTransferEvidenceProjector());
        services.AddSingleton<IPreCoderGateValidator>(new PreCoderGateValidator());
        services.AddSingleton<IDocumentTemplateService>(new DocumentTemplateService(timeProvider: time));
        services.AddSingleton<IWorkflowStudioService>(new WorkflowStudioService(
            graphValidator: new WorkflowGraphValidator(),
            templateStore: new InMemoryWorkflowTemplateStore(),
            timeProvider: time));

        services.AddAppUi();

        return services.BuildServiceProvider();
    }

    private sealed class RunObservationHarness
    {
        public RunObservationHarness()
        {
            CheckoutLockService = new CheckoutLockService(
                new RunObservationProjectLockRepository(),
                new RunObservationInstanceGuard(),
                new FixedTimeProvider(Now));
        }

        public StubWorkflowRunStore Runs { get; } = new();

        public RunObservationSessionStore Sessions { get; } = new();

        public RunObservationExecutionStore Executions { get; } = new();

        public ICheckoutLockService CheckoutLockService { get; }

        public InMemoryWorkflowPackageRepository Packages { get; } = new();

        public InMemoryWorkflowVersionRepository Versions { get; } = new();

        public InMemoryProjectRepository Projects { get; } = new();

        public InMemoryWorkflowBindingRepository Bindings { get; } = new();

        public void AddProject(string projectId) =>
            Projects.UpsertAsync(new Project(
                projectId,
                $"Project {projectId}",
                $@"C:\work\{projectId}",
                null,
                isDirty: false,
                hasRequiredInstructions: true,
                defaultWorkflowId: null,
                defaultRoutePolicyId: null,
                DataClassification.PrivateSource)).GetAwaiter().GetResult();

        public void AddPackage(string packageId) =>
            Packages.UpsertAsync(new WorkflowPackage(
                packageId,
                $"Package {packageId}",
                "Imported workflow",
                new[] { "workflow" },
                WorkflowSourceType.ZipArchive,
                Hash('a'),
                Hash('a'),
                Now,
                Now)).GetAwaiter().GetResult();

        public void AddVersion(string packageId, string versionId, int versionNumber, string[] roles) =>
            Versions.UpsertAsync(new WorkflowVersion(
                versionId,
                packageId,
                versionNumber,
                Hash('a'),
                Hash('b'),
                WorkflowSourceType.ZipArchive,
                entrypointsJson: null,
                declaredRolesJson: JsonSerializer.Serialize(roles),
                bindingsJson: null,
                compatibilityReportJson: null,
                creationMetadataJson: null,
                Now.AddDays(-1),
                activatedAtUtc: null)).GetAwaiter().GetResult();

        /// <summary>Points the active version of a project at one of the package versions.</summary>
        public void AddBinding(string projectId, string packageId, string activeVersionId) =>
            Bindings.UpsertAsync(new WorkflowBinding(
                Guid.NewGuid().ToString("N"),
                projectId,
                packageId,
                activeVersionId,
                routePolicyId: null,
                Now,
                Now)).GetAwaiter().GetResult();

        /// <summary>
        /// A run pinned to an assigned template version. The source workflow version and the template
        /// identity are two separate things and the screen has to show both.
        /// </summary>
        public WorkflowRun AddPinnedRun(
            string projectId,
            string workflowVersionId,
            string packageId,
            string runId,
            string templateId,
            int templateVersion)
        {
            var startedAt = Now.AddHours(-2);

            var run = WorkflowRun.StartPinnedToTemplate(
                runId,
                projectId,
                packageId,
                workflowVersionId,
                sessionId: null,
                CreateStage("node-a", "Role node-a", "node-b"),
                startedAt,
                templateId,
                templateVersion,
                """{"entryNodeId":"node-a","nodes":[{"nodeId":"node-a","kind":"Prompt","displayName":"Node node-a","roleBinding":"Role node-a","requiredCapabilities":[],"fallbackRouteIds":[],"retryBudget":0,"successTargetNodeId":"node-b"},{"nodeId":"node-b","kind":"TerminalOutcome","displayName":"Node node-b","roleBinding":"Role node-b","requiredCapabilities":[],"fallbackRouteIds":[],"retryBudget":0,"successTargetNodeId":null}]}""",
                """{"initialStageId":"node-a","stages":[{"stageId":"node-a","displayName":"Node node-a","requiredRole":"Role node-a","stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null,"nextStageId":"node-b","failureStageId":null},{"stageId":"node-b","displayName":"Node node-b","requiredRole":"Role node-b","stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null,"nextStageId":null,"failureStageId":null}]}""");

            Runs.Runs[runId] = run;

            return run;
        }

        public WorkflowRun AddRun(
            string projectId,
            string workflowVersionId,
            string packageId,
            string runId,
            string sessionId)
        {
            var architecture = CreateStage("stage-architecture", "Architect", "stage-review");
            var review = CreateStage("stage-review", "Reviewer", nextStageId: null);
            var startedAt = Now.AddHours(-2);

            var run = WorkflowRun.Start(
                runId,
                projectId,
                packageId,
                workflowVersionId,
                sessionId,
                architecture,
                startedAt);

            run.AdvanceTo(architecture, review, "Architecture document delivered.", startedAt.AddMinutes(10));

            Runs.Runs[runId] = run;

            return run;
        }

        public void AddSession(
            string sessionId,
            string projectId,
            string workflowRunId,
            string role,
            string modelId,
            string accountId,
            string? executionMode)
        {
            Sessions.Sessions[sessionId] = new Session(
                sessionId,
                new SessionBinding(
                    BackendType.OpenCode,
                    "prov-1",
                    accountId,
                    modelId,
                    reasoningEffort: null,
                    speedMode: null,
                    executionMode),
                projectId,
                $@"C:\work\{projectId}",
                $"native-{sessionId}",
                SessionState.Active,
                ReconciliationOutcome.None,
                CloseReason.None,
                continuationOfSessionId: null,
                forkedFromSessionId: null,
                workflowRunId,
                role,
                activeExecutionId: null,
                Now.AddHours(-2),
                Now.AddMinutes(-5));
        }

        public void ReplaceRunSessionMode(string sessionId, string? executionMode)
        {
            var existing = Sessions.Sessions[sessionId];
            var binding = existing.Binding;

            Sessions.Sessions[sessionId] = new Session(
                existing.Id,
                new SessionBinding(
                    binding.Backend,
                    binding.ProviderProfileId,
                    binding.AccountId,
                    binding.ModelId,
                    binding.ReasoningEffort,
                    binding.SpeedMode,
                    executionMode),
                existing.ProjectId,
                existing.WorkspaceRootPath,
                existing.NativeSessionId,
                existing.State,
                existing.ReconciliationOutcome,
                existing.CloseReason,
                existing.ContinuationOfSessionId,
                existing.ForkedFromSessionId,
                existing.WorkflowRunId,
                existing.Role,
                existing.ActiveExecutionId,
                existing.CreatedAt,
                existing.LastEventAt);
        }

        public void AddExecution(
            string executionId,
            string sessionId,
            ExecutionState state,
            DateTimeOffset startedAt,
            DateTimeOffset? endedAt = null) =>
            Executions.Executions[executionId] = new Execution(
                executionId,
                sessionId,
                $"req-{executionId}",
                state,
                ExecutionFailureReason.None,
                "route-opencode",
                "route-opencode",
                retryOfExecutionId: null,
                processState: null,
                exitCode: null,
                terminationReason: null,
                Array.Empty<string>(),
                sourceHashBefore: null,
                sourceHashAfter: null,
                startedAt,
                startedAt,
                endedAt);

        public WorkflowLibraryViewModel CreateLibrary() =>
            new(
                packageRepository: Packages,
                versionRepository: Versions,
                bindingRepository: Bindings,
                projectRepository: Projects,
                timelineService: new WorkflowRunTimelineService(),
                studioService: new WorkflowStudioService(timeProvider: new FixedTimeProvider(Now)),
                documentTemplateService: new DocumentTemplateService(timeProvider: new FixedTimeProvider(Now)),
                preCoderGateValidator: new PreCoderGateValidator(),
                runRepository: Runs,
                sessionRepository: Sessions,
                executionRepository: Executions,
                checkoutLockService: CheckoutLockService,
                transferEvidenceProjector: new RoleTransferEvidenceProjector(),
                timeProvider: new FixedTimeProvider(Now));

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

        private static string Hash(char character) => "sha256:" + new string(character, 64);
    }
}

/// <summary>Deterministic workflow run store for the console observation tests.</summary>
internal sealed class StubWorkflowRunStore : IWorkflowRunRepository
{
    public Dictionary<string, WorkflowRun> Runs { get; } = new(StringComparer.Ordinal);

    public List<string> ActiveQueries { get; } = new();

    public int Writes { get; private set; }
    public Exception? ReadFailure { get; set; }

    public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        Writes++;
        Runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task SaveArtifactAsync(
        WorkflowRun run,
        WorkflowArtifactEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        Writes++;
        Runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Runs.GetValueOrDefault(id));

    public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowRun>>(
            Runs.Values.Where(run => run.ProjectId == projectId).ToList());

    public Task<WorkflowRun?> GetActiveByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ActiveQueries.Add(projectId);
        if (ReadFailure is not null) return Task.FromException<WorkflowRun?>(ReadFailure);

        return Task.FromResult(Runs.Values
            .Where(run => run.ProjectId == projectId && !run.IsTerminal)
            .OrderByDescending(run => run.StartedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .FirstOrDefault());
    }
}

/// <summary>Deterministic session store for the console observation tests.</summary>
internal sealed class RunObservationSessionStore : ISessionRepository
{
    public Dictionary<string, Session> Sessions { get; } = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public Task UpsertAsync(Session session, CancellationToken cancellationToken = default)
    {
        Writes++;
        Sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<Session?> GetByIdAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sessions.GetValueOrDefault(sessionId));

    public Task<IReadOnlyList<Session>> ListByProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            Sessions.Values
                .Where(session => session.ProjectId == projectId)
                .OrderBy(session => session.Id, StringComparer.Ordinal)
                .ToList());

    public Task<IReadOnlyList<Session>> ListByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            Sessions.Values
                .Where(session => session.Binding.AccountId == accountId)
                .OrderBy(session => session.Id, StringComparer.Ordinal)
                .ToList());

    public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sessions.Remove(sessionId));
}

/// <summary>Deterministic execution store for the console observation tests.</summary>
internal sealed class RunObservationExecutionStore : IExecutionRepository
{
    public Dictionary<string, Execution> Executions { get; } = new(StringComparer.Ordinal);

    public List<string> SessionQueries { get; } = new();

    public int Writes { get; private set; }

    public Task UpsertAsync(Execution execution, CancellationToken cancellationToken = default)
    {
        Writes++;
        Executions[execution.Id] = execution;
        return Task.CompletedTask;
    }

    public Task<Execution?> GetByIdAsync(string executionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Executions.GetValueOrDefault(executionId));

    public Task<IReadOnlyList<Execution>> ListBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        SessionQueries.Add(sessionId);

        return Task.FromResult<IReadOnlyList<Execution>>(Executions.Values
            .Where(execution => execution.SessionId == sessionId)
            .OrderBy(execution => execution.Id, StringComparer.Ordinal)
            .ToList());
    }

    public Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The console observation tests never append execution events.");

    public Task<IReadOnlyList<ExecutionEventRecord>> ListEventsAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExecutionEventRecord>>(Array.Empty<ExecutionEventRecord>());
}

/// <summary>
/// Instance guard double. The writer-lock policy under test is the pure
/// <c>RequiresWriterLock</c> decision, so the supervisor permission path is never reached.
/// </summary>
internal sealed class RunObservationInstanceGuard : IApplicationInstanceGuard
{
    public string InstanceId => "run-observation-test";

    public bool IsPrimarySupervisor => true;

    public bool IsViewOnly => false;

    public void EnsureSupervisorPermitted()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Project lock repository double; the read-only policy never acquires a lock.</summary>
internal sealed class RunObservationProjectLockRepository : IProjectLockRepository
{
    public Task<ProjectLock?> GetActiveByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectLock?>(null);

    public Task<bool> TryAcquireAsync(ProjectLock projectLock, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The console observation tests never acquire a writer lock.");

    public Task<bool> ReleaseAsync(
        string lockId,
        DateTimeOffset releasedAt,
        string reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The console observation tests never release a writer lock.");
}
