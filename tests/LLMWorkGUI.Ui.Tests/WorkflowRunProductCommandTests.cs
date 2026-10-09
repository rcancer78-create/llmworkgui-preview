using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
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
/// Evidence for the shipped product run path (ROADMAP 10G): the Workflows screen and the Workflow Console
/// start and advance a real, persisted run through the composed <see cref="IWorkflowRunService"/>.
///
/// The whole happy path runs against a migrated SQLite database and the repositories the product
/// registers, so the assertions are about stored rows rather than about a view model that was handed its
/// own object. Every refusal case is asserted the same way: a named visible reason and a run count that
/// did not move. The Studio's coder-gate demonstration is asserted to stay separate from this path, and a
/// window without a run service is asserted to offer nothing at all rather than inventing a run.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed partial class WorkflowRunProductCommandTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";

    /// <summary>
    /// A second project bound to the same package and version, used to change the project selection without
    /// changing anything else the start action depends on.
    /// </summary>
    private const string SecondProjectId = "project-2";

    /// <summary>
    /// A package the library never loaded, used to move the package selection on its own: the picker
    /// refuses nothing, and the version the command captured is put back by hand afterwards.
    /// </summary>
    private const string OtherPackageId = "package-2";

    private const string VersionOneId = "version-1";
    private const string VersionTwoId = "version-2";
    private const string TemplateId = "linear-template";
    private const string ArtifactKind = "ReviewedDocument";

    /// <summary>
    /// A real persisted route, seeded alongside the project. A pinned model-review stage is authorized only by
    /// a reviewer execution whose requested and observed routes are a stored <c>Routes</c> row, so this is
    /// what the recorded verdicts in this file are read out of.
    /// </summary>
    private const string ReviewerRouteId = "route-run-product";
    private const string ReviewerProfileId = "profile-run-product";
    private const string ReviewerAccountId = "account-run-product";
    private const string ReviewerModelId = "model-run-product";

    /// <summary>
    /// A fixed instant in the past, written to the observed run so that a run started later in the same
    /// test is unambiguously the newer active one without relying on clock resolution between two calls.
    /// </summary>
    private const string EarlierStart = "2020-01-01T00:00:00.0000000+00:00";

    private static readonly DateTimeOffset SeededAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "llmworkgui-phase10g-" + Guid.NewGuid().ToString("N"));

    private readonly string _appData;

    private readonly SqliteConnectionFactory _factory;

    public WorkflowRunProductCommandTests()
    {
        _appData = Path.Combine(_root, "appdata");
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));

        // A migrated database and the seeded project/package/version/binding, created through the real
        // composition so the screen later resolves against the same registered singletons.
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
            catch (System.IO.IOException)
            {
                // A leftover temp directory must never turn a run-command assertion red.
            }
        }
    }

    [Fact]
    public async Task TheProductCommandStartsARealRunPinnedToTheAssignedTemplateAndObservesBothIdentities()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        // The console follows the binding, so v2 - the project's actually active source version - is
        // selected without the operator touching anything.
        Assert.Equal(VersionTwoId, library.SelectedVersion!.Id);
        Assert.True(library.CanStartAssignedRun);
        Assert.False(library.CanAdvanceObservedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);

        await library.StartAssignedRunAsync();

        Assert.True(library.HasObservedRun);
        Assert.Empty(library.Blocker);

        var run = library.ObservedRun!;
        Assert.Equal(ProjectId, run.ProjectId);
        Assert.Equal(PackageId, run.WorkflowPackageId);
        Assert.Equal(VersionTwoId, run.WorkflowVersionId);
        Assert.Equal(TemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal("node-a", run.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, run.State);

        // The two identities are reported as two different things on the screen: the run itself names the
        // source version it started from, and the template display names the assignment it was pinned to.
        Assert.Equal(VersionTwoId, library.ObservedRun!.WorkflowVersionId);
        Assert.Equal($"{TemplateId}@1", library.ObservedRunTemplateDisplay);
        Assert.Contains(TemplateId, library.RunCommandStateDisplay, StringComparison.Ordinal);
        Assert.Contains(run.Id, library.ObservedRunDisplay, StringComparison.Ordinal);

        // The run is a stored row, not a view model object that merely looks persisted.
        Assert.Equal(1, await CountRunsAsync());

        // The console's own run graph follows the observed run and stays read-only.
        Assert.True(library.Studio.HasRunGraph);
        Assert.Contains(run.Id, library.Studio.RunGraphSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped built-in standard template is startable through this screen, and the screen reports the
    /// plan it pinned: the assigned version, the stage the run sits on, the artifact that stage requires and
    /// the failure route the pinned scheme declares. The declared route is shown together with the fact that
    /// this build does not take it, so nothing on the screen offers to move the run along it.
    /// </summary>
    [Fact]
    public async Task TheProductCommandStartsTheAssignedBuiltInTemplateAndShowsItsPinnedPlan()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignBuiltInStandardTemplateAsync(host.Services);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        Assert.True(library.CanStartAssignedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunFailureRouteDisplay);

        await library.StartAssignedRunAsync();

        var run = library.ObservedRun!;
        Assert.Equal(WorkflowStudioService.StandardTemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Empty(library.Blocker);

        // The pinned version, the current stage and the gate requirement all come from the observed run.
        Assert.Equal($"{WorkflowStudioService.StandardTemplateId}@1", library.ObservedRunTemplateDisplay);
        Assert.Contains(run.Id, library.RunCommandStateDisplay, StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.TaskSpecificationStageId, library.RunCommandStateDisplay, StringComparison.Ordinal);
        Assert.Contains("TaskSpecificationDocument", library.StageArtifactRequirementDisplay, StringComparison.Ordinal);

        // The first stage declares no failure target, and that is what the screen says.
        Assert.Contains("не объявляет маршрут отказа", library.ObservedRunFailureRouteDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("маршрут отказа", library.RunCommandStateDisplay, StringComparison.Ordinal);

        // The run walks its own chain: a stored document is the whole of the first stage, and the advance is
        // refused by the run's own gate until one exists.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => library.RunService!.AdvanceStageAsync(run.Id, "operator pressed advance"));
        Assert.Contains("TaskSpecificationDocument", refused.Message, StringComparison.Ordinal);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, library.ObservedRun!.CurrentStageId);

        await library.RunService!.RecordStageArtifactAsync(
            run.Id,
            WorkflowScheme.TaskSpecificationStageId,
            "TaskSpecificationDocument",
            new MemoryStream("the task"u8.ToArray(), writable: false),
            DataClassification.PrivateSource);

        Assert.True(library.CanAdvanceObservedRun);
        await library.AdvanceObservedRunAsync();

        Assert.Equal(WorkflowScheme.ArchitectureStageId, library.ObservedRun!.CurrentStageId);
        Assert.Contains("ArchitectureDocument", library.StageArtifactRequirementDisplay, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage of the built-in chain that declares a failure target shows the target and states that this
    /// build does not take it. The target is the run's own declared edge, and the screen offers no control
    /// that would move the run along it.
    /// </summary>
    [Fact]
    public async Task AStageWithADeclaredFailureRouteShowsItAndSaysItIsNotTaken()
    {
        using var host = await CreateInitializedHostAsync();
        var canonical = WorkflowStudioService.CreateStandardTemplate();
        var graph = new WorkflowGraph(canonical.Graph.EntryNodeId,
            canonical.Graph.Nodes.Select(node => new WorkflowNodeDefinition(
                node.NodeId, node.Kind, node.DisplayName, node.RoleBinding,
                node.RequiredCapabilities, node.PrimaryRouteId, node.FallbackRouteIds, node.Timeout,
                node.RetryBudget, node.SuccessTargetNodeId,
                node.NodeId == WorkflowScheme.MultiLevelReviewStageId
                    ? WorkflowScheme.FinalOutcomeStageId : node.FailureTargetNodeId,
                node.ConditionExpression, node.ArtifactContract, node.PermissionIntent, node.GateMetadata)).ToArray());
        var custom = new WorkflowTemplateDefinition("explicit-forward-failure", 1, "Explicit failure route",
            "Acyclic failure route display control", graph, canonical.RoleBindings,
            canonical.RequiredDocumentTemplates, isBuiltIn: false, SeededAt);
        var store = host.Services.GetRequiredService<IWorkflowTemplateStore>();
        await store.SaveAsync(custom);
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-forward-failure", ProjectId, custom.TemplateId, custom.Version, SeededAt));

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runService = library.RunService!;
        var runId = library.ObservedRun!.Id;

        // Walk to the custom multi-level review with an explicit acyclic forward failure edge,
        // and re-observe: the screen shows what the repository holds, not what the walk last returned.
        await WalkToStageAsync(host.Services, runId, WorkflowScheme.MultiLevelReviewStageId);
        await library.ObserveActiveRunAsync();

        var run = library.ObservedRun!;
        Assert.Equal(WorkflowScheme.MultiLevelReviewStageId, run.CurrentStageId);

        Assert.Contains(WorkflowScheme.FinalOutcomeStageId, library.ObservedRunFailureRouteDisplay, StringComparison.Ordinal);
        Assert.Contains("не исполняется", library.ObservedRunFailureRouteDisplay, StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.FinalOutcomeStageId, library.RunCommandStateDisplay, StringComparison.Ordinal);
        Assert.Contains("не исполняется", library.RunCommandStateDisplay, StringComparison.Ordinal);

        // And the attempt itself is refused by name, with the run left where it is.
        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceToDeclaredFailureStageAsync(runId, "the review failed"));
        Assert.Contains(
            WorkflowRunFailureRouteRefusals.FailureRouteNotExecutable,
            refused.Message,
            StringComparison.Ordinal);
        Assert.Equal(WorkflowScheme.MultiLevelReviewStageId, (await ReloadRunAsync(host.Services, runId)).CurrentStageId);
    }

    /// <summary>
    /// Walks a run pinned to the shipped built-in chain forward until it stands on
    /// <paramref name="targetStageId"/>, satisfying every gate the chain declares on the way: the stored
    /// artifact, the reviewer verdicts and the local approval.
    /// <para>
    /// The reviewer turns are written by <see cref="TestOnlyReviewerTurns"/>, a test-only stand-in for the
    /// external model - the production reviewer channel refuses every route in this build, so no screen test
    /// can produce a real one. Nothing here is evidence that a model reviewed anything; what it produces is
    /// the persisted rows a linked verdict is authorized by, and the gate still refuses a verdict without
    /// them.
    /// </para>
    /// </summary>
    private static async Task WalkToStageAsync(
        IServiceProvider services,
        string runId,
        string targetStageId)
    {
        var runService = services.GetRequiredService<IWorkflowRunService>();
        var runRepository = services.GetRequiredService<IWorkflowRunRepository>();
        var reviewerTurns = new TestOnlyReviewerTurns();

        while (true)
        {
            var run = (await runRepository.GetByIdAsync(runId))!;

            if (string.Equals(run.CurrentStageId, targetStageId, StringComparison.Ordinal))
            {
                return;
            }

            var stage = WorkflowSchemeSnapshot
                .Deserialize(run.TemplateSchemeSnapshotJson!, run.Id)
                .Scheme
                .GetRequiredStage(run.CurrentStageId);

            Assert.NotNull(stage.ArtifactRequirement);

            var recorded = await runService.RecordStageArtifactAsync(
                runId,
                stage.StageId,
                stage.ArtifactRequirement!,
                new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes($"bytes for {stage.ArtifactRequirement}"),
                    writable: false),
                DataClassification.PrivateSource);

            var artifact = WorkflowArtifactEvidence.SelectCurrent(
                recorded.Artifacts,
                runId,
                stage.StageId,
                stage.ArtifactRequirement!)!;

            foreach (var role in stage.RequiredReviewerRoles)
            {
                await reviewerTurns.ApproveAsync(services, runId, stage.StageId, role, artifact);
            }

            if (stage.RequiresUserApproval)
            {
                await runService.RecordUserApprovalAsync(
                    runId,
                    new UserApprovalEvidence(
                        Guid.NewGuid().ToString("N"),
                        "test-only",
                        stage.StageId,
                        artifact.HashSha256,
                        UserApprovalDecision.Approved,
                        "approved",
                        DateTimeOffset.UtcNow));
            }

            await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(services, runId, $"Historical fixture traversal of {stage.StageId}; product review remains refused.");
        }
    }

    /// <summary>
    /// Test-only stand-in for the external model reviewer. It reuses the file's own
    /// <see cref="PersistReviewerExecutionAsync"/>, which writes the persisted rows a real backend turn would
    /// leave, then seeds a historical caller-authored claim after asserting product refusal. It is not a reviewer or channel; no
    /// test using it is evidence that a model reviewed anything.
    /// </summary>
    private sealed class TestOnlyReviewerTurns
    {
        public async Task ApproveAsync(
            IServiceProvider services,
            string runId,
            string stageId,
            string role,
            WorkflowArtifactEvidence artifact)
        {
            var now = DateTimeOffset.UtcNow;
            var executionId = await PersistReviewerExecutionAsync(
                services,
                runId,
                role,
                stageId,
                artifact,
                now);

            await HistoricalWorkflowReviewFixture.SeedAsync(
                services, runId,
                new ReviewerVerdictRecord(
                    role,
                    ReviewerRouteId,
                    artifact.HashSha256,
                    WorkflowReviewVerdict.Approve,
                    "Historical fixture claim; no model response or parsed verdict exists.",
                    now,
                    executionId,
                    stageId,
                    artifact.ArtifactId));
        }
    }

    private static async Task<WorkflowRun> ReloadRunAsync(IServiceProvider services, string runId) =>

        (await services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId))!;

    private static async Task AssignBuiltInStandardTemplateAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<IWorkflowTemplateStore>();

        // The store seeds the shipped version on first use, so the project only has to be pointed at it.
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-built-in",
            ProjectId,
            WorkflowStudioService.StandardTemplateId,
            1,
            SeededAt));
    }

    [Fact]
    public async Task BothPinnedIdentitiesSurviveAReopenOfTheScreenAndAReopenOfTheDatabase()

    {
        using (var host = await CreateInitializedHostAsync())
        {
            await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

            var library = CreateLibrary(host.Services);
            await library.RefreshAsync();
            await library.StartAssignedRunAsync();
        }

        // A brand new service provider and a brand new screen over the same database: nothing is carried
        // over in memory, so the identities below come from storage alone.
        using var reopened = await CreateInitializedHostAsync();
        var reopenedLibrary = CreateLibrary(reopened.Services);
        await reopenedLibrary.RefreshAsync();

        Assert.True(reopenedLibrary.HasObservedRun);
        Assert.Equal(VersionTwoId, reopenedLibrary.ObservedRun!.WorkflowVersionId);
        Assert.Equal(TemplateId, reopenedLibrary.ObservedRun.TemplateId);
        Assert.Equal(1, reopenedLibrary.ObservedRun.TemplateVersion);
        Assert.Equal($"{TemplateId}@1", reopenedLibrary.ObservedRunTemplateDisplay);
    }

    [Fact]
    public async Task TheProductCommandAdvancesTheObservedRunOnceOnItsOwnPinnedScheme()
    {
        using var host = await CreateInitializedHostAsync();

        // Version 1 is node-a -> node-x -> node-y and version 2 is node-a -> node-b -> node-c, so the
        // stage the run lands on proves which template version it was actually advanced against.
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-x", "node-y" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        Assert.True(library.CanAdvanceObservedRun);

        await library.AdvanceObservedRunAsync();

        Assert.Empty(library.Blocker);
        Assert.Equal("node-x", library.ObservedRun!.CurrentStageId);
        Assert.Equal(runId, library.ObservedRun.Id);
        Assert.Equal(1, library.ObservedRun.TemplateVersion);
        Assert.Equal(WorkflowRunState.Running, library.ObservedRun.State);

        // The stored run moved; the pinned identity and snapshots did not.
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        Assert.Equal("node-x", stored!.CurrentStageId);
        Assert.Equal(TemplateId, stored.TemplateId);
        Assert.Equal(1, stored.TemplateVersion);
    }

[Fact]
    public async Task ARunSittingOnItsLastStageIsNotAdvancedAndIsNotDeclaredComplete()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        await library.AdvanceObservedRunAsync();

        // The run is now on the TerminalOutcome stage but its state is still Running: reaching the last
        // stage is a stage, not a workflow outcome.
        var run = library.ObservedRun!;
        Assert.Equal("node-b", run.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.False(run.IsTerminal);

        // The pinned current stage has no NextStageId, so there is nothing left to advance to. That is an
        // enablement fact and not only a click-time refusal: the button and the command are unoffered,
        // even though the run is observed, non-terminal and Running.
        Assert.False(library.CanAdvanceObservedRun);
        Assert.False(library.AdvanceObservedRunCommand.CanExecute(null));

        await library.AdvanceObservedRunAsync();

        // A direct invocation still fails closed, and it fails closed with the named reason instead of
        // silently doing nothing.
        Assert.Contains("последней в закреплённой схеме", library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);

        // Nothing was completed, failed or cancelled behind the operator's back.
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(run.Id);
        Assert.Equal(WorkflowRunState.Running, stored!.State);
        Assert.Equal(WorkflowTerminalOutcome.None, stored.TerminalOutcome);
        Assert.Null(stored.EndedAtUtc);
    }

    [Fact]
    public async Task ATerminalRunIsNeverAdvancedAgain()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var runId = library.ObservedRun!.Id;
        await runService.CancelRunAsync(runId, "explicit terminal transition");

        await library.ObserveActiveRunAsync();

        // A finished run is no longer the project's active run, so the console stops showing it and the
        // advance command has nothing to act on.
        Assert.False(library.HasObservedRun);
        Assert.False(library.CanAdvanceObservedRun);

        await library.AdvanceObservedRunAsync();

        Assert.Equal(1, await CountRunsAsync());
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        Assert.Equal(WorkflowRunState.Cancelled, stored!.State);

        // It was explicitly cancelled at its first stage; no successful workflow outcome is claimed.
        Assert.Empty(stored.Transitions);
    }

    [Fact]
    public async Task ASelectableButInactiveSourceVersionCreatesNoRunAndNamesTheActiveOne()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        // v1 exists and is selectable, but the binding still says v2 is the active version.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionOneId);

        Assert.True(library.CanStartAssignedRun);

        await library.StartAssignedRunAsync();

        Assert.Contains("не является активной", library.Blocker, StringComparison.Ordinal);
        Assert.Contains(VersionTwoId, library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task AnUnboundProjectCreatesNoRunAndSaysSo()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        await ExecuteAsync("DELETE FROM WorkflowBindings;");

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        // The console falls back to the first version when a project has no binding, so the operator does
        // see a plausible selection - and the command still refuses it.
        Assert.NotNull(library.SelectedVersion);
        Assert.False(library.HasCurrentBinding);

        await library.StartAssignedRunAsync();

        Assert.Contains("не привязан", library.Blocker, StringComparison.Ordinal);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task AProjectWithoutATemplateAssignmentCreatesNoRunAndKeepsTheNamedBlocker()
    {
        using var host = await CreateInitializedHostAsync();

        // The template version is saved but the project is never pointed at it, so the run service itself
        // refuses. The screen has to surface that name rather than a generic failure.
        await host.Services.GetRequiredService<IWorkflowTemplateStore>().SaveAsync(
            CreateLinearTemplate(TemplateId, 1, new[] { "node-a", "node-b" }));

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        await library.StartAssignedRunAsync();

        Assert.Contains(
            WorkflowTemplateExecutionBlockers.MissingAssignment,
            library.Blocker,
            StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.False(library.HasObservedRun);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task AnUnsupportedAssignedGraphCreatesNoRunAndKeepsTheNamedBlocker()
    {
        using var host = await CreateInitializedHostAsync();

        var templateStore = host.Services.GetRequiredService<IWorkflowTemplateStore>();

        // A gate node is a storable graph this slice deliberately does not execute.
        await templateStore.SaveAsync(new WorkflowTemplateDefinition(
            "gated-template",
            1,
            "Gated",
            "A graph with a gate.",
            new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition("node-b", WorkflowNodeKind.ApprovalGate, "Node B", "Role B", successTargetNodeId: "node-c"),
                Terminal("node-c")
            }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            SeededAt));

        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-gated",
            ProjectId,
            "gated-template",
            1,
            SeededAt));

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        await library.StartAssignedRunAsync();

        Assert.Contains(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            library.Blocker,
            StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task ASecondRunIsRefusedWhileTheProjectStillHasANonTerminalRunOfAnotherVersion()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        // A non-terminal run of v1 exists for the project. The console shows v2 as active, so this run is
        // invisible there - and it still blocks a second run for the same project.
        var existing = await host.Services.GetRequiredService<IWorkflowRunService>()
            .StartLegacyRunAsync(ProjectId, PackageId, VersionOneId);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        Assert.False(library.HasObservedRun);
        Assert.Equal(1, await CountRunsAsync());

        await library.StartAssignedRunAsync();

        Assert.Contains("уже есть активный запуск", library.Blocker, StringComparison.Ordinal);
        Assert.Contains(existing.Id, library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Equal(1, await CountRunsAsync());
        Assert.False(library.HasObservedRun);
    }

    [Fact]
    public async Task AStartAfterTheProjectRunIsTerminalIsAllowedAgain()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var existing = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);
        await runService.CancelRunAsync(existing.Id, "explicit terminal transition");

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        // A finished run is a normal reason to start the next one, so the action is offered again and says
        // nothing about being unavailable.
        Assert.True(library.CanStartAssignedRun);
        Assert.Empty(library.StartAssignedRunUnavailableReason);
        Assert.False(library.HasStartAssignedRunUnavailableReason);

        await library.StartAssignedRunAsync();

        Assert.Empty(library.Blocker);
        Assert.Equal(2, await CountRunsAsync());
        Assert.NotEqual(existing.Id, library.ObservedRun!.Id);
        Assert.Equal(WorkflowRunState.Running, library.ObservedRun.State);
    }

    /// <summary>
    /// The duplicate start the console can see is taken away from the operator instead of being offered and
    /// refused on click, and the reason is the run that is in the way rather than a mute "unavailable".
    /// </summary>
    [Fact]
    public async Task ASecondStartOnTheObservedNonTerminalRunIsUnofferedAndNamesTheRunInItsWay()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        Assert.True(library.CanStartAssignedRun);
        Assert.Empty(library.StartAssignedRunUnavailableReason);
        Assert.True(library.StartAssignedRunCommand.CanExecute(null));

        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;

        Assert.False(library.CanStartAssignedRun);
        Assert.False(library.StartAssignedRunCommand.CanExecute(null));
        Assert.True(library.HasStartAssignedRunUnavailableReason);
        Assert.Contains(runId, library.StartAssignedRunUnavailableReason, StringComparison.Ordinal);
        Assert.Contains(
            library.ObservedRun.CurrentStageId,
            library.StartAssignedRunUnavailableReason,
            StringComparison.Ordinal);
        Assert.Contains("Запуск недоступен", library.StartAssignedRunUnavailableReason, StringComparison.Ordinal);

        // Advance is untouched by the duplicate-start condition: the run still has somewhere to go.
        Assert.True(library.CanAdvanceObservedRun);
    }

    /// <summary>
    /// A refusal that only exists as a disabled button is not a refusal - the authoritative duplicate-run
    /// check stays reachable, so a direct invocation still fails closed by name and still creates nothing.
    /// </summary>
    [Fact]
    public async Task ADirectSecondStartIsStillRefusedByNameAndCreatesNothing()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        var before = await CountRunsAsync();

        await library.StartAssignedRunAsync();

        Assert.Contains("уже есть активный запуск", library.Blocker, StringComparison.Ordinal);
        Assert.Contains(runId, library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Equal(before, await CountRunsAsync());
        Assert.Equal(runId, library.ObservedRun!.Id);
    }

    /// <summary>
    /// The enablement follows the selection and the observation, never a value cached at load time: switching
    /// to a version the open run was not started from makes the button offered again - and that start is
    /// then refused by the repository re-read, which is the case this screen cannot see for itself.
    /// <para>
    /// The half of the rule that is easy to get wrong is the half before the observation returns. A setter
    /// only starts an asynchronous reload; until that finishes the screen is still holding the previous
    /// project's run, and offering Start on that basis is a claim about a project nobody is looking at. So
    /// the action is taken away the moment the selection moves and comes back only when the observation for
    /// the new selection has finished - proved here against a real project with an active run, a real
    /// project without one, a version change, and an observation that comes back for a selection that has
    /// already moved past it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDuplicateStartEnablementFollowsSelectionAndAsyncObservation()
    {
        var observation = new RunObservationGate();

        using var host = await CreateInitializedHostAsync(observationGate: observation);
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });
        await SeedSecondProjectWithoutRunsAsync();

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;

        Assert.False(library.CanStartAssignedRun);

        // The active pointer moves to another version, so the open run stops being the observed one.
        await ExecuteAsync(
            "UPDATE WorkflowBindings SET ActiveVersionId = $version WHERE ProjectId = $project "
                + "AND WorkflowPackageId = $package;",
            ("$version", VersionOneId),
            ("$project", ProjectId),
            ("$package", PackageId));

        await library.RefreshAsync();

        Assert.False(library.HasObservedRun);
        Assert.Empty(library.StartAssignedRunUnavailableReason);
        Assert.True(library.CanStartAssignedRun);

        // Offered, and still refused by name - an invisible run of another version is not this screen's
        // business to pre-empt, it is the repository re-read's.
        await library.StartAssignedRunAsync();

        Assert.Contains("уже есть активный запуск", library.Blocker, StringComparison.Ordinal);
        Assert.Contains(runId, library.Blocker, StringComparison.Ordinal);
        Assert.Equal(1, await CountRunsAsync());

        // And the enablement comes back to the observed state after the refusal re-observes the same run.
        library.SelectedVersion = library.Versions.First(version => version.Id == VersionTwoId);
        await library.ObserveActiveRunAsync();

        Assert.Equal(runId, library.ObservedRun!.Id);
        Assert.False(library.CanStartAssignedRun);

        // A project with no run at all: the action is taken away the moment the selection moves, says why,
        // and comes back only once this selection's own observation has finished and found nothing.
        observation.Arm();

        library.SelectedProject = library.Projects.First(project => project.Id == SecondProjectId);

        // Nothing has been awaited yet and the reload the setter started is provably still inside the
        // product's own run repository, so what is asserted here is the screen's own fail-closed behaviour
        // and not the accident of a fast query.
        await observation.Entered;

        AssertFailsClosedOnThePendingObservation(library);

        observation.Open();

        await library.ObserveActiveRunAsync();

        Assert.False(library.HasObservedRun);
        Assert.True(library.CanStartAssignedRun);
        Assert.Empty(library.StartAssignedRunUnavailableReason);

        // Put the active pointer back, so the seeded project is once again a project whose bound active
        // version has an open run of its own and the trip below is the mirror image of the one above.
        await ExecuteAsync(
            "UPDATE WorkflowBindings SET ActiveVersionId = $version WHERE ProjectId = $project "
                + "AND WorkflowPackageId = $package;",
            ("$version", VersionTwoId),
            ("$project", ProjectId),
            ("$package", PackageId));

        // The other direction, and the stale half. The observation of the project that has an open run is
        // still outstanding when the selection moves back to the one that has none, so its answer arrives
        // for a selection the screen has already left. Adopting it would put a run that belongs to another
        // project on screen and, on the next refresh, take the action away for a project that has none.
        observation.Arm();

        library.SelectedProject = library.Projects.First(project => project.Id == ProjectId);

        await observation.Entered;

        AssertFailsClosedOnThePendingObservation(library);

        library.SelectedProject = library.Projects.First(project => project.Id == SecondProjectId);

        AssertFailsClosedOnThePendingObservation(library);

        observation.Open();

        await library.ObserveActiveRunAsync();

        Assert.False(library.HasObservedRun);
        Assert.True(library.CanStartAssignedRun);
        Assert.Empty(library.StartAssignedRunUnavailableReason);

        // And back to the project that does have an open run: the same window, closed the same way, and a
        // reason that names the run rather than the wait once the observation has caught up.
        observation.Arm();

        library.SelectedProject = library.Projects.First(project => project.Id == ProjectId);

        await observation.Entered;

        AssertFailsClosedOnThePendingObservation(library);

        observation.Open();

        await library.ObserveActiveRunAsync();

        Assert.Equal(runId, library.ObservedRun!.Id);
        Assert.False(library.CanStartAssignedRun);
        Assert.Contains(runId, library.StartAssignedRunUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("Запуск недоступен", library.StartAssignedRunUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fail-closed shape the duplicate-start action has to hold for the whole of a pending observation:
    /// unoffered, unclickable, and not mute about why.
    /// </summary>
    private static void AssertFailsClosedOnThePendingObservation(WorkflowLibraryViewModel library)
    {
        Assert.False(library.CanStartAssignedRun);
        Assert.False(library.StartAssignedRunCommand.CanExecute(null));
        Assert.Contains(
            "наблюдение активного запуска выбранной версии ещё выполняется",
            library.StartAssignedRunUnavailableReason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A second project bound to the same package and version, with no run at all: the half of the rule that
    /// has to come back to offered once the new selection's observation has actually finished.
    /// </summary>
    private async Task SeedSecondProjectWithoutRunsAsync()
    {
        await ExecuteAsync(
            """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($project, 'Run product project without runs', 'C:\project-2', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowBindings (Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('9b1c2d3e-4f50-4a6b-8c9d-0e1f2a3b4c5d', $project, $package, $version, NULL, $now, $now);
            """,
            ("$project", SecondProjectId),
            ("$package", PackageId),
            ("$version", VersionTwoId),
            ("$now", "2026-09-29T00:00:00Z"));
    }



    [Fact]
    public async Task AnAdvanceAfterTheProjectsActiveVersionChangesIsRefusedByName()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        // The operator moves the active pointer to a version the observed run was not started from.
        await ExecuteAsync(
            "UPDATE WorkflowBindings SET ActiveVersionId = $version WHERE ProjectId = $project "
                + "AND WorkflowPackageId = $package;",
            ("$version", VersionOneId),
            ("$project", ProjectId),
            ("$package", PackageId));

        var runId = library.ObservedRun!.Id;

        await library.AdvanceObservedRunAsync();

        Assert.Contains("не является активной", library.Blocker, StringComparison.Ordinal);

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        Assert.Equal("node-a", stored!.CurrentStageId);
        Assert.Empty(stored.Transitions);
    }

    [Fact]
    public async Task AnUnpinnedLegacyRunIsNotAdvancedByTheProductCommand()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var legacy = await runService.StartLegacyRunAsync(ProjectId, PackageId, VersionTwoId);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.ObserveActiveRunAsync();

        Assert.NotNull(library.ObservedRun);
        Assert.Equal(legacy.Id, library.ObservedRun!.Id);
        Assert.False(library.ObservedRun.IsTemplateBacked);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunTemplateDisplay);

        await library.AdvanceObservedRunAsync();

        Assert.Contains("не закреплён за версией шаблона", library.Blocker, StringComparison.Ordinal);

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(legacy.Id);
        Assert.Equal(WorkflowRunState.Running, stored!.State);
        Assert.Empty(stored.Transitions);
    }

    [Fact]
    public async Task TheStudioCoderGateDemonstrationStaysSeparateFromTheRealRunPath()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();

        // The document-gate demonstration is a separate check on the Studio panel. It never creates a run,
        // never clears the console and never changes what the product run commands would do.
        await library.Studio.RequestCoderStartAsync();
        await library.Studio.RequestCoderStartAsync();

        Assert.Equal(0, await CountRunsAsync());
        Assert.False(library.HasObservedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.True(library.CanStartAssignedRun);
        Assert.False(library.CanAdvanceObservedRun);

        // The gate's own verdict is reported on the Studio, and it is not a run outcome.
        Assert.Empty(library.StatusMessage);
    }

    [Fact]
    public async Task AWindowWithoutARunServiceOffersNoRunCommandAndInventsNoRun()
    {
        var library = new WorkflowLibraryViewModel();

        Assert.False(library.IsRunExecutionAvailable);
        Assert.False(library.CanStartAssignedRun);
        Assert.False(library.CanAdvanceObservedRun);
        Assert.False(library.StartAssignedRunCommand.CanExecute(null));
        Assert.False(library.AdvanceObservedRunCommand.CanExecute(null));
        Assert.Contains("не настроена", library.RunCommandNotice, StringComparison.Ordinal);

        await library.StartAssignedRunAsync();
        await library.AdvanceObservedRunAsync();

        Assert.Empty(library.Blocker);
        Assert.Empty(library.StatusMessage);
        Assert.False(library.HasObservedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.RunCommandStateDisplay);
    }

    [Fact]
    public async Task TheCommandsAreOfferedOnlyWhenASelectionAndAnObservedRunActuallyAllowThem()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b" });

        var library = CreateLibrary(host.Services);

        // Nothing selected yet: neither command is offered.
        Assert.False(library.CanStartAssignedRun);
        Assert.False(library.CanAdvanceObservedRun);

        await library.RefreshAsync();
        Assert.True(library.CanStartAssignedRun);
        Assert.False(library.CanAdvanceObservedRun);

        await library.StartAssignedRunAsync();
        Assert.True(library.CanAdvanceObservedRun);

        // Clearing selection on the same DI singleton clears its observed run and disables advance.
        Assert.Same(library, CreateLibrary(host.Services));
        library.SelectedProject = null;
        library.SelectedVersion = null;
        await library.ObserveActiveRunAsync();

        Assert.False(library.HasObservedRun);
        Assert.False(library.CanAdvanceObservedRun);
    }

    [Fact]
    public void TheShippedWorkflowsScreenRendersTheProductRunButtons()
    {
        var library = new WorkflowLibraryViewModel(new InMemoryWorkflowPackageRepository(),
            new InMemoryWorkflowVersionRepository(), new InMemoryWorkflowBindingRepository());

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
var buttons = FindVisualDescendants<Button>(root)
                    .Where(button => button.Name is "LibraryStartRunButton" or "LibraryAdvanceRunButton")
                    .ToArray();

                Assert.Equal(2, buttons.Length);

                // A window without the composed run service still ships the buttons, but they are
                // disabled and say why - the screen never looks runnable when it is not.
                Assert.All(buttons, button => Assert.False(button.IsEnabled));

                // The project, package and version pickers are bound to the freeze flag, so a click cannot
                // switch the selection underneath a run command that is already committing its side effect.
                var pickers = FindVisualDescendants<FrameworkElement>(root)
                    .Where(element => element.Name is "LibraryProjectPicker"
                        or "LibraryPackagesPicker"
                        or "LibraryVersionsPicker")
                    .ToArray();

                Assert.Equal(3, pickers.Length);

                Assert.All(pickers, picker =>
                {
                    Assert.Equal(library.IsRunSelectionEditable, picker.IsEnabled);
                    Assert.Equal(
                        "IsRunSelectionEditable",
                        BindingOperations.GetBindingExpression(picker, FrameworkElement.IsEnabledProperty)
                            ?.ParentBinding.Path.Path);
                });

                var texts = ReadTextBlocks(root);
                Assert.Contains(library.RunCommandNotice, texts);
                Assert.Contains(library.ObservedRunDisplay, texts);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheShippedAssignedReviewPanelRendersOneButtonNoInputAndTheNoticeThatSaysSo()
    {
        var library = new WorkflowLibraryViewModel();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                var buttons = FindVisualDescendants<Button>(root)
                    .Where(button => button.Name == "LibraryRequestAssignedReviewButton")
                    .ToArray();

                Assert.Single(buttons);

                // A window without the composed review service still ships the button, but it is disabled and
                // the panel says why - the screen never looks runnable when it is not.
                Assert.False(buttons[0].IsEnabled);

                // No role picker, no route field, no document field and no hash box. Every one of those is
                // exactly what the request has to resolve for itself, and a control for any of them would be
                // an operator's word standing in for a pinned assignment.
                var inputs = FindVisualDescendants<FrameworkElement>(root)
                    .Where(element => element.Name is "LibraryAssignedReviewRoleBox"
                        or "LibraryAssignedReviewRouteBox"
                        or "LibraryAssignedReviewHashBox"
                        or "LibraryAssignedReviewDocumentBox")
                    .ToArray();

                Assert.Empty(inputs);

                var texts = ReadTextBlocks(root);
                Assert.Contains(library.AssignedReviewRequirementDisplay, texts);
                Assert.Contains(library.AssignedReviewStatusDisplay, texts);
                Assert.Contains(library.AssignedReviewNotice, texts);

                // The notice states the three refusals the action can produce and says outright that the
                // action creates no verdict, so the panel's own contract is visible without a click.
                Assert.Contains("не создаёт", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("route-opencode", library.AssignedReviewNotice, StringComparison.Ordinal);

                // And it names the one thing that decides the outcome today: the registered reviewer channel
                // supports no route, because the observed identifiers do not reverse-map to a unique stored
                // Routes.Id. A panel that stayed silent there would leave an operator to guess whether a
                // component is broken. The notice states that in the operator's own words; the exact channel
                // identifier stays where the operator inspects it, in the named refusal the service returns.
                Assert.Contains("Канал ревью прокси в режиме «только чтение»", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("идентификатора провайдера", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("пути Codex", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("идентификатором модели провайдера", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("размерности режима", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.Contains("ни сессии, ни исполнения", library.AssignedReviewNotice, StringComparison.Ordinal);
                Assert.DoesNotContain("star-cliproxy-review-readonly", library.AssignedReviewNotice, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task RequestingAnAssignedReviewRefusesTheDocumentedRouteAndRecordsNothing()
    {
        // The product action, end to end: the run is pinned to a template that binds its required reviewer
        // role to 'route-opencode', the artifact is stored and its bytes verified, and the request still
        // refuses - because that id is not a persisted Routes row. Nothing is written, and in particular no
        // verdict and no pending reviewer turn.
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(
            host.Services,
            ArtifactKind,
            requiredReviewerRoles: new[] { "Reviewer" },
            roleBindings: new[] { new RoleBindingDefinition("Reviewer", "route-opencode", modelId: "model-1") });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        library.StageArtifactPath = WriteArtifactFile("review-me.md", Encoding.UTF8.GetBytes("the document"));
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
        Assert.True(library.CanRequestAssignedReview);

        await library.RequestAssignedReviewAsync();

        var result = library.AssignedReviewResult!;
        Assert.True(result.IsRefusal);

        var role = Assert.Single(result.Roles);
        Assert.Equal("Reviewer", role.Role);
        Assert.Equal("route-opencode", role.AssignedRouteId);
        Assert.Contains("is not a persisted route", role.Refusal!, StringComparison.Ordinal);
        Assert.Null(role.ExecutionId);
        Assert.Null(role.ObservedRouteId);

        // The screen reports the refusal in full and says the action produced no verdict.
        Assert.Contains("is not a persisted route", library.AssignedReviewDetailDisplay, StringComparison.Ordinal);
        Assert.Contains("Вердикт этим действием не создаётся", library.StatusMessage, StringComparison.Ordinal);

        // And storage is unchanged: no session, no execution, no binding and no verdict on the run.
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetByIdAsync(library.ObservedRun!.Id);
        Assert.Empty(stored!.Verdicts);
        Assert.Empty(
            await host.Services.GetRequiredService<IWorkflowReviewEvidenceRepository>()
                .ListByRunIdAsync(library.ObservedRun.Id));
        Assert.Empty(await host.Services.GetRequiredService<ISessionRepository>().ListByProjectAsync(ProjectId));
    }

    [Fact]
    public async Task RequestingAnAssignedReviewOverARealRouteRefusesWithTheMissingRouteIdentityAndRecordsNothing()
    {
        // The route is a real stored Routes row with a real provider profile, account and model behind it, and
        // the run is pinned to a template that binds its required reviewer role to exactly that row. The
        // request still refuses - because nothing in this build can say which stored route a gateway served -
        // and the refusal names the identity that is missing instead of reporting a dispatch.
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(
            host.Services,
            ArtifactKind,
            requiredReviewerRoles: new[] { "Reviewer" },
            roleBindings: new[] { new RoleBindingDefinition("Reviewer", ReviewerRouteId, modelId: ReviewerModelId) });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        library.StageArtifactPath = WriteArtifactFile("review-me.md", Encoding.UTF8.GetBytes("the document"));
        await library.AttachStageArtifactAsync();

        Assert.True(library.CanRequestAssignedReview);

        await library.RequestAssignedReviewAsync();

        var result = library.AssignedReviewResult!;
        Assert.True(result.IsRefusal);
        Assert.Equal(0, result.DispatchedRoleCount);

        var role = Assert.Single(result.Roles);
        Assert.Equal(ReviewerRouteId, role.AssignedRouteId);
        Assert.Contains("Declined:", role.Refusal!, StringComparison.Ordinal);
        Assert.Contains("star-cliproxy-review-readonly", role.Refusal!, StringComparison.Ordinal);
        Assert.Contains("resolved to one persisted Routes.Id", role.Refusal!, StringComparison.Ordinal);
        Assert.Contains("trusted per-turn response-origin proof", role.Refusal!, StringComparison.Ordinal);
        Assert.Null(role.ExecutionId);
        Assert.Null(role.ObservedRouteId);
        Assert.False(role.ObservedAssignedRoute);

        // The shipped status line reports the refusal in full and says the action produced no verdict.
        Assert.Contains("Declined:", library.AssignedReviewDetailDisplay, StringComparison.Ordinal);
        Assert.Contains("Вердикт этим действием не создаётся", library.StatusMessage, StringComparison.Ordinal);

        // And storage is unchanged: no session, no execution, no binding and no verdict on the run.
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetByIdAsync(library.ObservedRun!.Id);
        Assert.Empty(stored!.Verdicts);
        Assert.Empty(
            await host.Services.GetRequiredService<IWorkflowReviewEvidenceRepository>()
                .ListByRunIdAsync(library.ObservedRun.Id));
        Assert.Empty(await host.Services.GetRequiredService<ISessionRepository>().ListByProjectAsync(ProjectId));
    }

    [Fact]
    public void TheShippedWorkflowConsoleRendersTheProductRunButtons()

    {
        var library = new WorkflowLibraryViewModel();
        var console = new WorkflowConsolidatedViewModel(library);

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var view = new WorkflowConsoleView { DataContext = console };
            var host = new ContentControl { Content = view };

            var window = new Window
            {
                Width = 1400,
                Height = 900,
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

            window.Show();
            host.Measure(new Size(1400, 900));
            host.Arrange(new Rect(0, 0, 1400, 900));
            host.UpdateLayout();

            try
            {
                var buttons = FindVisualDescendants<Button>(root: view)
                    .Where(button => button.Name is "ConsoleStartRunButton" or "ConsoleAdvanceRunButton")
                    .ToArray();

                Assert.Equal(2, buttons.Length);
                Assert.All(buttons, button => Assert.False(button.IsEnabled));

                // The console drives the very same library instance the Workflows screen shows, so the two
                // surfaces can never disagree about which run is running.
                Assert.Same(library, console.Library);
                Assert.Same(library.Studio, console.Studio);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The live-control evidence for the freeze the three pickers claim. The shipped Workflows
    /// <c>DataTemplate</c> is rendered against the very view model the run command drives, and the
    /// composed <see cref="IWorkflowRunService"/> is suspended inside the start itself, so the assertion
    /// is taken in the exact window the freeze exists for: the command has captured its identities and
    /// committed nothing back to the screen yet.
    ///
    /// The command runs on the dispatcher rather than being awaited on the test thread, because the
    /// binding update under test is delivered there; blocking that thread would starve the very update
    /// this test exists to prove. The gate is awaited from the test thread instead, which leaves the
    /// dispatcher free to deliver it.
    /// </summary>
    [Fact]
    public async Task TheShippedPickersAreDisabledForTheWholeBusyWindowOfAnAwaitedRunCommand()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        Assert.True(library.CanStartAssignedRun);

        StaTestRunner.EnsureApplication();

        Window? window = null;
        FrameworkElement[] pickers = Array.Empty<FrameworkElement>();

        try
        {
            StaTestRunner.Run(() =>
            {
                new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

                var opened = OpenTemplatePanel(library);
                window = opened.Window;

                pickers = FindVisualDescendants<FrameworkElement>(opened.Root)
                    .Where(element => element.Name is "LibraryProjectPicker"
                        or "LibraryPackagesPicker"
                        or "LibraryVersionsPicker")
                    .ToArray();

                // The same three pickers are live before the command, so a "false" below is the command's
                // doing and not a control that never worked.
                Assert.Equal(3, pickers.Length);
                Assert.All(pickers, picker => Assert.True(picker.IsEnabled));
            });

            gate.Arm();

            // Started, not awaited: the command suspends inside the gated run service and hands the
            // dispatcher back, which is what keeps the WPF binding pipeline alive during the assertions.
            var start = StaTestRunner.Run(() => library.StartAssignedRunAsync());

            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));

            (string Name, bool IsEnabled)[] busy = StaTestRunner.Run(() =>
            {
                DrainDispatcher();

                return pickers
                    .Select(picker => (Name: picker.Name ?? string.Empty, IsEnabled: picker.IsEnabled))
                    .ToArray();
            });

            // The command is still suspended, so the whole awaited window is covered rather than a moment
            // inside it, and the flag alone is not what is asserted - the controls' own IsEnabled is.
            Assert.False(start.IsCompleted);
            Assert.False(library.IsRunSelectionEditable);

            Assert.All(busy, state => Assert.False(
                state.IsEnabled,
                $"Picker {state.Name} stayed enabled while the run command was awaiting the run service."));

            gate.Open();
            await start;

            (string Name, bool IsEnabled)[] completed = StaTestRunner.Run(() =>
            {
                DrainDispatcher();

                return pickers
                    .Select(picker => (Name: picker.Name ?? string.Empty, IsEnabled: picker.IsEnabled))
                    .ToArray();
            });

            // The freeze ends with the command instead of sticking: the run was persisted and the operator
            // gets the pickers back.
            Assert.Equal(1, await CountRunsAsync());
            Assert.True(library.IsRunSelectionEditable);

            Assert.All(completed, state => Assert.True(
                state.IsEnabled,
                $"Picker {state.Name} stayed disabled after the run command completed."));
        }
        finally
        {
            var opened = window;

            if (opened is not null)
            {
                StaTestRunner.Run(() => opened.Close());
            }
        }
    }

    /// <summary>
    /// Runs the dispatcher queue that is already waiting, so a control read cannot overtake a binding
    /// update the same dispatcher still owes. A cross-thread notification is posted rather than applied
    /// inline, which is exactly the case a bare read would miss.
    /// </summary>
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();

        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle,
            new DispatcherOperationCallback(state => ((DispatcherFrame)state!).Continue = false),
            frame);

        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public async Task AStartAlreadyPersistedIsNeverReportedAsRefusedWhenTheSelectionMovesDuringTheAwait()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        Assert.Equal(VersionTwoId, library.SelectedVersion!.Id);

        gate.Arm();

        var start = library.StartAssignedRunAsync();

        // The command is now suspended inside the composed run service. This is the window the pickers
        // are frozen for, so a click could not switch the selection underneath a committed side effect.
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(library.IsRunSelectionEditable);

        // A programmatic selection change still gets through, and that is the case the screen has to
        // answer truthfully instead of with a refusal.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionOneId);

        gate.Open();
        await start;

        // The start was decided before the selection moved and it is a stored row, on the version the
        // command captured - not on whatever is selected now.
        Assert.Equal(1, await CountRunsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetActiveByProjectIdAsync(ProjectId);

        Assert.NotNull(stored);
        Assert.Equal(VersionTwoId, stored!.WorkflowVersionId);
        Assert.Equal("node-a", stored.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, stored.State);

        // Nothing is refused and nothing is denied. The screen names the run that was persisted, the
        // identities it was created under, and states plainly that the selection on screen now differs.
        Assert.Empty(library.Blocker);
        Assert.Contains(stored.Id, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(ProjectId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(VersionTwoId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("изменился", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(stored.Id, library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не создан", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не создан", library.StatusMessage, StringComparison.Ordinal);

        // The observation is allowed to follow the current selection and clear itself, but its empty state
        // must not overwrite the notice about what really happened.
        Assert.False(library.HasObservedRun);
        Assert.Equal(WorkflowLibraryViewModel.UnavailableIndicator, library.ObservedRunDisplay);
        Assert.Contains(stored.Id, library.RunCommandNotice, StringComparison.Ordinal);

        // Switching back finds the run that was created all along, next to the same truthful notice.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionTwoId);
        await library.ObserveActiveRunAsync();

        Assert.Equal(stored.Id, library.ObservedRun!.Id);
        Assert.Contains(stored.Id, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.True(library.IsRunSelectionEditable);
    }

    [Fact]
    public async Task AnAdvanceAlreadyPersistedIsNeverReportedAsRefusedWhenTheSelectionMovesDuringTheAwait()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        Assert.Equal("node-a", library.ObservedRun.CurrentStageId);

        gate.Arm();

        var advance = library.AdvanceObservedRunAsync();

        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(library.IsRunSelectionEditable);

        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionOneId);

        gate.Open();
        await advance;

        // The transition happened before the selection moved, and the stored run says so.
        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

        Assert.Equal("node-b", stored!.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, stored.State);
        Assert.Equal(VersionTwoId, stored.WorkflowVersionId);

        Assert.Empty(library.Blocker);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(VersionTwoId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-a", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-b", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("изменился", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(runId, library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не продвинут", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не продвинут", library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не создан", library.RunCommandNotice, StringComparison.Ordinal);

        // The empty observation state does not replace the persisted-action notice.
        Assert.False(library.HasObservedRun);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The product stage-artifact path (ROADMAP 10A/10E): the Workflows screen copies the bytes of a local
    /// file into the run as the artifact the observed run's own pinned scheme requires at its current stage.
    ///
    /// Every case runs through a migrated SQLite database, the repositories the product registers and the
    /// real content-addressed blob store, so a green assertion is about stored bytes rather than about a
    /// view model that was handed its own object. The refusals are asserted the same way: a named reason,
    /// an artifact count that did not move, and - for every local file fault - a message that contains no
    /// path, no file name and no file content.
    /// </summary>
    [Fact]
    public async Task TheProductCommandCopiesTheChosenFileBytesIntoThePinnedStageArtifactAndUnblocksThatStage()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        Assert.Equal("node-a", library.ObservedRun.CurrentStageId);

        // The entry stage is gated on the artifact alone, so the transition this screen offers is refused
        // until the bytes exist. That refusal is the proof that the artifact is what the gate waits for.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Services.GetRequiredService<IWorkflowRunService>().AdvanceStageAsync(runId, "gate probe"));

        // The requirement is on screen before any file is chosen, and it is the kind the run's own pinned
        // snapshot declares rather than anything this test supplied.
        var pinnedKind = ReadPinnedArtifactKind(library.ObservedRun!, "node-a");
        Assert.NotNull(pinnedKind);
        Assert.Equal(ArtifactKind, pinnedKind!);
        Assert.Contains(runId, library.StageArtifactRequirementDisplay, StringComparison.Ordinal);
        Assert.Contains("node-a", library.StageArtifactRequirementDisplay, StringComparison.Ordinal);
        Assert.Contains(pinnedKind!, library.StageArtifactRequirementDisplay, StringComparison.Ordinal);
        Assert.Contains("адресное хранилище", library.StageArtifactNotice, StringComparison.Ordinal);

        Assert.Equal(DataClassification.PrivateSource, library.StageArtifactClassification);
        Assert.Equal(
            new[] { DataClassification.PublicSource, DataClassification.PrivateSource, DataClassification.Restricted },
            library.StageArtifactClassifications);
        Assert.True(library.CanAttachStageArtifact);
        Assert.Equal(0, await CountArtifactsAsync());

        var bytes = Encoding.UTF8.GetBytes("task specification bytes for the pinned stage");
        var expectedHash = ContentHash(bytes);
        library.StageArtifactPath = WriteArtifactFile("task-specification.md", bytes);

        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
        Assert.False(string.IsNullOrWhiteSpace(library.StatusMessage));

        // The message names the captured run, stage and kind and the hash the run service returned, and
        // nothing it was not told.
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-a", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(pinnedKind!, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.StatusMessage, StringComparison.Ordinal);

        // The evidence is a stored row carrying the recorded bytes' own hash and size, not a locally
        // built one, and the bytes themselves are in the content-addressed store under that hash.
        Assert.Equal(1, await CountArtifactsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        var evidence = Assert.Single(stored!.Artifacts);

        Assert.Equal(runId, evidence.RunId);
        Assert.Equal("node-a", evidence.StageId);
        Assert.Equal(pinnedKind, evidence.Kind);
        Assert.Equal(expectedHash, evidence.HashSha256);
        Assert.Equal(bytes.Length, evidence.SizeBytes);
        Assert.Equal(DataClassification.PrivateSource, evidence.Classification);
        Assert.True(await host.Services.GetRequiredService<IWorkflowArtifactBlobStore>()
            .VerifyAsync(expectedHash, CancellationToken.None));

        // The observed panels follow the repository, so the requirement line now reports the recorded hash.
        Assert.Contains(expectedHash, library.StageArtifactRequirementDisplay, StringComparison.Ordinal);
        Assert.Equal(expectedHash, library.ObservedRun!.Artifacts.Single().HashSha256);

        // Only now does the transition the screen offers go through, which is what the artifact was for.
        await library.AdvanceObservedRunAsync();

        Assert.Empty(library.Blocker);
        Assert.Equal("node-b", library.ObservedRun!.CurrentStageId);
    }

    [Fact]
    public async Task TheAttachedBytesAndHashSurviveAReopenOfTheScreenAndTheDatabase()
    {
        var bytes = Encoding.UTF8.GetBytes("durable artifact bytes");
        var expectedHash = ContentHash(bytes);
        string runId;

        using (var host = await CreateInitializedHostAsync())
        {
            await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

            var library = CreateLibrary(host.Services);
            await library.RefreshAsync();
            await library.StartAssignedRunAsync();

            runId = library.ObservedRun!.Id;
            library.StageArtifactPath = WriteArtifactFile("durable.md", bytes);
            await library.AttachStageArtifactAsync();

            Assert.Empty(library.Blocker);
            Assert.Contains(expectedHash, library.RunCommandNotice, StringComparison.Ordinal);
        }

        // A brand new service provider and a brand new screen over the same database and the same blob
        // store: nothing is carried over in memory, so what follows comes from storage alone.
        using var reopened = await CreateInitializedHostAsync();

        var stored = await reopened.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        var evidence = Assert.Single(stored!.Artifacts);

        Assert.Equal(expectedHash, evidence.HashSha256);
        Assert.Equal(bytes.Length, evidence.SizeBytes);
        Assert.Equal(DataClassification.PrivateSource, evidence.Classification);

        // The bytes themselves are re-read out of the content-addressed store and compared, so a row that
        // survived without its content could not pass.
        await using var storedBytes = await reopened.Services.GetRequiredService<WorkflowBlobStore>()
            .GetBlobStreamAsync(expectedHash, CancellationToken.None);
        using var copy = new MemoryStream();
        await storedBytes.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());

        // The reopened screen observes the same run and the same requirement, and the gate the artifact
        // satisfied is still satisfied.
        var reopenedLibrary = CreateLibrary(reopened.Services);
        await reopenedLibrary.RefreshAsync();

        Assert.Equal(runId, reopenedLibrary.ObservedRun!.Id);
        Assert.Contains(expectedHash, reopenedLibrary.StageArtifactRequirementDisplay, StringComparison.Ordinal);

        await reopenedLibrary.AdvanceObservedRunAsync();

        Assert.Empty(reopenedLibrary.Blocker);
        Assert.Equal("node-b", reopenedLibrary.ObservedRun!.CurrentStageId);
    }

    /// <summary>
    /// The action is offered for a run whose own pinned current stage declares an artifact and for nothing
    /// else. A gate-free pinned stage has nothing to attach to, so the button is unoffered and a direct
    /// invocation fails closed with the same named reason instead of a silent no-op.
    /// </summary>
    [Fact]
    public async Task TheArtifactActionIsUnofferedForARunWhosePinnedStageDeclaresNoArtifact()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignLinearTemplateAsync(host.Services, version: 1, nodeIds: new[] { "node-a", "node-b", "node-c" });

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        Assert.Equal("node-a", library.ObservedRun!.CurrentStageId);
        Assert.Null(ReadPinnedArtifactKind(library.ObservedRun!, "node-a"));
        Assert.False(library.CanAttachStageArtifact);
        Assert.Contains("не объявляет артефакт", library.StageArtifactRequirementDisplay, StringComparison.Ordinal);

        // The command itself still refuses out loud rather than doing nothing: the file is real, so only
        // the missing requirement can be the reason.
        library.StageArtifactPath = WriteArtifactFile("gate-free.md", Encoding.UTF8.GetBytes("bytes"));

        await library.AttachStageArtifactAsync();

        Assert.Contains("не объявляет артефакт", library.Blocker, StringComparison.Ordinal);
        Assert.Equal(0, await CountArtifactsAsync());
    }

    [Fact]
    public async Task AttachingAgainstAStoredRunThatAlreadyMovedIsRefusedAndNothingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;

        // The stage is moved on by the composed run service itself, so the stored row is exactly what the
        // product would have written. The screen is deliberately not re-observed, so it still holds the
        // run as it was at node-a: the captured stage is now the stale one.
        library.StageArtifactPath = WriteArtifactFile("first-stage.md", Encoding.UTF8.GetBytes("first stage bytes"));
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);

        await host.Services.GetRequiredService<IWorkflowRunService>().AdvanceStageAsync(runId, "moved on");
        Assert.Equal("node-a", library.ObservedRun!.CurrentStageId);

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

        Assert.Equal("node-b", stored!.CurrentStageId);
        Assert.False(stored.IsTerminal);

        library.StageArtifactPath = WriteArtifactFile("stale-stage.md", Encoding.UTF8.GetBytes("bytes"));

        await library.AttachStageArtifactAsync();

        Assert.Contains(runId, library.Blocker, StringComparison.Ordinal);
        Assert.Contains("node-a", library.Blocker, StringComparison.Ordinal);
        Assert.Contains("node-b", library.Blocker, StringComparison.Ordinal);

        // Only the artifact recorded for node-a exists; none was written for the stage that has moved on.
        Assert.Equal(1, await CountArtifactsAsync());
        Assert.Equal("node-a", (await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetByIdAsync(runId))!.Artifacts.Single().StageId);
    }

    [Fact]
    public async Task AttachingWhenANewerActiveRunReplacedTheObservedOneIsRefusedAndNothingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var observedId = library.ObservedRun!.Id;
        library.StageArtifactPath = WriteArtifactFile("replaced.md", Encoding.UTF8.GetBytes("bytes"));

        // A second run of the same project starts while the first is still nonterminal, and it is the
        // newer of the two. The console keeps observing the old one, so only a re-read of the project's
        // active run can notice that the row the bytes would land on is no longer the current one.
        var replacement = await host.Services.GetRequiredService<IWorkflowRunService>()
            .StartRunAsync(ProjectId, PackageId, VersionTwoId);

        await ExecuteAsync(
            "UPDATE WorkflowRuns SET StartedAtUtc = $started WHERE Id = $run;",
            ("$started", EarlierStart),
            ("$run", observedId));

        var repository = host.Services.GetRequiredService<IWorkflowRunRepository>();
        var active = await repository.GetActiveByProjectIdAsync(ProjectId);
        var observed = await repository.GetByIdAsync(observedId);

        // The premise of the case: the newer run is the active one and the observed run is still
        // nonterminal, so the refusal cannot be explained by the old run having ended.
        Assert.NotNull(active);
        Assert.Equal(replacement.Id, active!.Id);
        Assert.NotEqual(observedId, active.Id);
        Assert.False(observed!.IsTerminal);

        // The screen still holds the old run, so the action is still offered - and is still refused.
        Assert.Equal(observedId, library.ObservedRun!.Id);
        Assert.True(library.CanAttachStageArtifact);

        await library.AttachStageArtifactAsync();

        Assert.Contains(observedId, library.Blocker, StringComparison.Ordinal);
        Assert.Contains(replacement.Id, library.Blocker, StringComparison.Ordinal);
        Assert.Equal(0, await CountArtifactsAsync());
    }

    [Fact]
    public async Task AMissingArtifactFileIsRefusedWithAMessageThatNamesNoPath()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var missing = Path.Combine(_root, "artifacts", "absent-report.md");
        library.StageArtifactPath = missing;

        await library.AttachStageArtifactAsync();

        AssertNamesNoFileDetail(library.Blocker, missing, "absent-report.md", "absent-report");
        AssertNoFileDetail(library.StatusMessage, missing, "absent-report.md", "absent-report");
        AssertNoFileDetail(library.RunCommandNotice, missing, "absent-report.md", "absent-report");
        Assert.Empty(library.StatusMessage);
        Assert.Equal(0, await CountArtifactsAsync());
    }

    [Fact]
    public async Task AnArtifactPathThatNamesADirectoryIsRefusedAsADirectoryAndNothingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var directory = Path.Combine(_root, "artifacts", "collected-evidence");
        Directory.CreateDirectory(directory);
        library.StageArtifactPath = directory;

        await library.AttachStageArtifactAsync();

        Assert.Contains("каталог", library.Blocker, StringComparison.OrdinalIgnoreCase);
        AssertNoFileDetail(library.Blocker, directory, "collected-evidence");
        AssertNoFileDetail(library.RunCommandNotice, directory, "collected-evidence");
        Assert.Equal(0, await CountArtifactsAsync());
    }

    [Fact]
    public async Task AnUnreadableArtifactFileIsRefusedWithAMessageThatNamesNoPath()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var locked = WriteArtifactFile("locked-evidence.md", Encoding.UTF8.GetBytes("bytes"));

        // A handle that shares nothing makes the file unreadable to anyone else, which is the ordinary
        // reason an operator's own file cannot be read.
        await using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            library.StageArtifactPath = locked;

            await library.AttachStageArtifactAsync();
        }

        AssertNamesNoFileDetail(library.Blocker, locked, "locked-evidence.md", "locked-evidence");
        AssertNoFileDetail(library.RunCommandNotice, locked, "locked-evidence.md", "locked-evidence");
        Assert.Equal(0, await CountArtifactsAsync());
    }

    [Fact]
    public async Task AnOversizeArtifactFileIsRefusedBeforeAnythingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        // The seeded import package is already in the store, so the assertion is about what this command
        // did and did not add to it.
        var blobsBefore = CountBlobs();

        var oversize = Path.Combine(_root, "artifacts", "oversize-evidence.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(oversize)!);

        await using (var file = new FileStream(oversize, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // One byte over the bound, and no more: the refusal has to come from the bound itself.
            file.SetLength(WorkflowLibraryViewModel.MaxStageArtifactBytes + 1);
        }

        library.StageArtifactPath = oversize;

        await library.AttachStageArtifactAsync();

        Assert.Contains(
            (WorkflowLibraryViewModel.MaxStageArtifactBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture),
            library.Blocker,
            StringComparison.Ordinal);
        AssertNoFileDetail(library.Blocker, oversize, "oversize-evidence.bin", "oversize-evidence");
        AssertNoFileDetail(library.RunCommandNotice, oversize, "oversize-evidence.bin", "oversize-evidence");
        Assert.Equal(0, await CountArtifactsAsync());
        Assert.Equal(blobsBefore, CountBlobs());
    }

    /// <summary>
    /// The negative control: the only operator input is a path, so a well-formed content hash typed into
    /// it is still only a path. It is refused as the missing file it is, and no evidence row and no blob
    /// appear for the hash - the screen has no route by which a typed hash could ever be recorded.
    /// </summary>
    [Fact]
    public async Task AMadeUpHashTypedIntoThePathFieldIsRefusedAndRecordsNoArtifact()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var blobsBefore = CountBlobs();
        var invented = WorkflowArtifactEvidence.ContentHashPrefix
            + new string('a', WorkflowArtifactEvidence.Sha256HexLength);

        library.StageArtifactPath = invented;

        await library.AttachStageArtifactAsync();

        Assert.False(string.IsNullOrWhiteSpace(library.Blocker));
        AssertNoFileDetail(library.Blocker, invented);
        Assert.Equal(0, await CountArtifactsAsync());
        Assert.Equal(blobsBefore, CountBlobs());
        Assert.Empty(library.ObservedRun!.Artifacts);

        // The one hash the screen ever shows is the one the run service computed from real bytes.
        var bytes = Encoding.UTF8.GetBytes("real bytes");
        library.StageArtifactPath = WriteArtifactFile("real-evidence.md", bytes);
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
        Assert.NotEqual(invented, library.ObservedRun!.Artifacts.Single().HashSha256);
        Assert.Equal(ContentHash(bytes), library.ObservedRun!.Artifacts.Single().HashSha256);
    }

    /// <summary>
    /// The live-control evidence for the freeze the artifact inputs claim. The shipped Workflows
    /// <c>DataTemplate</c> is rendered against the very view model the command drives and the composed
    /// run service is suspended inside the attach itself, so the assertion is taken in the exact window
    /// the freeze exists for: the command has captured its path, its classification and its run, and has
    /// committed nothing back to the screen yet.
    /// </summary>
    [Fact]
    public async Task TheShippedArtifactPathAndClassificationAreDisabledForTheWholeBusyWindowOfAnAwaitedAttach()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        library.StageArtifactPath = WriteArtifactFile("busy-window.md", Encoding.UTF8.GetBytes("bytes"));

        StaTestRunner.EnsureApplication();

        Window? window = null;
        FrameworkElement root = null!;

        try
        {
            StaTestRunner.Run(() =>
            {
                new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

                var opened = OpenTemplatePanel(library);
                window = opened.Window;
                root = opened.Root;

                var elements = FindVisualDescendants<FrameworkElement>(opened.Root).ToArray();

                var pathBox = elements.Single(element => element.Name == "LibraryStageArtifactPathBox");
                var classificationPicker = elements.Single(element => element.Name == "LibraryStageArtifactClassificationPicker");

                // Both controls are live before the command, so a "false" below is the command's doing and
                // not a control that never worked.
                Assert.True(pathBox.IsEnabled);
                Assert.True(classificationPicker.IsEnabled);
                Assert.Equal(DataClassification.PrivateSource, ((ComboBox)classificationPicker).SelectedItem);
            });

            gate.Arm();

            // Started, not awaited: the command suspends inside the gated run service and hands the
            // dispatcher back, which is what keeps the WPF binding pipeline alive during the assertions.
            var attach = StaTestRunner.Run(() => library.AttachStageArtifactAsync());

            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));

            var busy = StaTestRunner.Run(() => ReadFrozenControls(root));

            // The command is still suspended, so the whole awaited window is covered rather than a moment
            // inside it, and the flag alone is not what is asserted - the controls' own IsEnabled is.
            Assert.False(attach.IsCompleted);
            Assert.False(library.IsStageArtifactInputEditable);
            Assert.False(library.IsRunSelectionEditable);
            Assert.Equal(FrozenControlNames.Length, busy.Length);
            Assert.All(busy, state => Assert.False(
                state.IsEnabled,
                $"Control {state.Name} stayed enabled while the artifact attach was awaiting the run service."));

            gate.Open();
            await attach;

            var completed = StaTestRunner.Run(() => ReadFrozenControls(root));

            // The freeze ends with the command instead of sticking: the bytes were stored and the operator
            // gets the inputs back.
            Assert.Equal(1, await CountArtifactsAsync());
            Assert.True(library.IsStageArtifactInputEditable);

            Assert.All(completed, state => Assert.True(
                state.IsEnabled,
                $"Control {state.Name} stayed disabled after the artifact attach completed."));
        }
        finally
        {
            var opened = window;

            if (opened is not null)
            {
                StaTestRunner.Run(() => opened.Close());
            }
        }
    }

    /// <summary>
    /// The controls the artifact command freezes while it is in flight: its own two inputs, the attach
    /// action and the three product pickers, all of which the command captured or must not be re-pointed
    /// under.
    /// </summary>
    private static readonly string[] FrozenControlNames =
    {
        "LibraryStageArtifactPathBox",
        "LibraryStageArtifactClassificationPicker",
        "LibraryAttachStageArtifactButton",
        "LibraryProjectPicker",
        "LibraryPackagesPicker",
        "LibraryVersionsPicker"
    };

    private static (string Name, bool IsEnabled)[] ReadFrozenControls(FrameworkElement root)
    {
        DrainDispatcher();

        return FindVisualDescendants<FrameworkElement>(root)
            .Where(element => FrozenControlNames.Contains(element.Name, StringComparer.Ordinal))
            .Select(element => (Name: element.Name ?? string.Empty, IsEnabled: element.IsEnabled))
            .ToArray();
    }

    [Fact]
    public async Task AnArtifactAlreadyPersistedIsNeverReportedAsRefusedWhenTheSelectionMovesDuringTheAwait()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        var bytes = Encoding.UTF8.GetBytes("persisted across a moving selection");
        var expectedHash = ContentHash(bytes);
        library.StageArtifactPath = WriteArtifactFile("moving-selection.md", bytes);

        gate.Arm();

        var attach = library.AttachStageArtifactAsync();

        // The command is now suspended inside the composed run service, holding the captured path, the
        // captured classification and the captured run. This is the window the inputs are frozen for.
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(library.IsStageArtifactInputEditable);

        // A programmatic selection change still gets through, and that is the case the screen has to
        // answer truthfully instead of with a refusal.
        library.SelectedVersion = library.Versions.Single(version => version.Id == VersionOneId);

        gate.Open();
        await attach;

        // The bytes were decided and stored before the selection moved, and the stored run says so.
        Assert.Equal(1, await CountArtifactsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

        Assert.Equal(expectedHash, Assert.Single(stored!.Artifacts).HashSha256);
        Assert.Equal("node-a", stored.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, stored.State);
        Assert.Equal(VersionTwoId, stored.WorkflowVersionId);

        // Nothing is refused and nothing is denied. The screen names the run that was persisted, the stage
        // and kind it was recorded under, the hash the service returned, and states plainly that the
        // selection on screen now differs.
        Assert.Empty(library.Blocker);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-a", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(ArtifactKind, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("изменился", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("прервана до записи", library.RunCommandNotice, StringComparison.Ordinal);

        // The observation is allowed to follow the current selection and clear itself, but its empty state
        // must not overwrite the notice about what really happened.
        Assert.False(library.HasObservedRun);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same window. Only the package moves while the composed run service holds the
    /// bytes, and the project and version on screen stay exactly the ones the command captured - the
    /// version view model deliberately does not have to be a member of the freshly reloaded
    /// <c>Versions</c> collection for this, which is what makes the package the single difference. A note
    /// that compared the live selection with itself would report nothing here, so the assertion on the
    /// note is the evidence that the captured package id is what the message is built from.
    /// </summary>
    [Fact]
    public async Task APackageThatMovesAfterTheCommitStillLeavesThePersistedHashAndTheDifferenceNoteVisible()
    {
        var gate = new RunServiceGate();

        using var host = await CreateInitializedHostAsync(gate);
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        var capturedVersion = library.Versions.Single(version => version.Id == VersionTwoId);
        var bytes = Encoding.UTF8.GetBytes("persisted across a moving package");
        var expectedHash = ContentHash(bytes);
        library.StageArtifactPath = WriteArtifactFile("moving-package.md", bytes);

        gate.Arm();

        var attach = library.AttachStageArtifactAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(library.IsStageArtifactInputEditable);

        library.SelectedPackage = new WorkflowPackageItemViewModel(new WorkflowPackage(
            OtherPackageId,
            "package-2.zip",
            description: null,
            tags: Array.Empty<string>(),
            sourceType: WorkflowSourceType.ZipArchive,
            originalHash: WorkflowBlobStore.ComputeBlobId(CreateImportedWorkflowZip()),
            originalBlobId: WorkflowBlobStore.ComputeBlobId(CreateImportedWorkflowZip()),
            createdAtUtc: SeededAt,
            updatedAtUtc: SeededAt));

        // The picker reloads the versions of whatever package is selected, so that reload is awaited here
        // rather than left racing: the version captured above is put back afterwards and has to survive.
        await library.LoadVersionsAsync();
        library.SelectedVersion = capturedVersion;

        Assert.NotEqual(PackageId, library.SelectedPackage!.Id);
        Assert.Equal(ProjectId, library.SelectedProject!.Id);
        Assert.Equal(VersionTwoId, library.SelectedVersion!.Id);

        gate.Open();
        await attach;

        // The package is the only thing that moved, and the stored run is still the one the command acted
        // on: the bytes landed on the captured run, stage and kind, not on whatever is selected now.
        Assert.Equal(1, await CountArtifactsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

        Assert.Equal(expectedHash, Assert.Single(stored!.Artifacts).HashSha256);
        Assert.Equal("node-a", stored.CurrentStageId);
        Assert.Equal(VersionTwoId, stored.WorkflowVersionId);

        // The screen still has nothing to refuse, and it now names the difference: a package-only move is
        // a difference from what was persisted, and the note has to say so.
        Assert.Empty(library.Blocker);
        Assert.NotEqual(PackageId, library.SelectedPackage!.Id);
        Assert.Equal(ProjectId, library.SelectedProject!.Id);
        Assert.Equal(VersionTwoId, library.SelectedVersion!.Id);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-a", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(ArtifactKind, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("изменился", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("прервана до записи", library.RunCommandNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing the operator's file is the one step of the attach path that happens after the run service
    /// has returned, so it is the one step that can fault after the artifact is committed. The window the
    /// run service read from releases its handle and then raises, and the screen has to come out of that
    /// with the stored row, the hash the service computed and the success notice intact - no refusal, no
    /// cleared status, and nothing from the IO stack.
    /// </summary>
    [Fact]
    public async Task AFailedCloseAfterTheCommittedWriteLeavesThePersistedHashAndTheSuccessNoticeStanding()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        var bytes = Encoding.UTF8.GetBytes("committed bytes behind a failing close");
        var expectedHash = ContentHash(bytes);
        var path = WriteArtifactFile("failing-close.md", bytes);
        var window = new FaultingCloseStream();

        library.StageArtifactPath = path;
        library.StageArtifactContentDecorator = window.Wrap;

        // Nothing escapes to the dispatcher: the command completes, and the fault is handled inside it.
        await library.AttachStageArtifactAsync();

        // The handle really was released before the close failed, so the window is not the leak here.
        Assert.True(window.InnerClosed);

        // The commit is untouched by the fault: one stored row, the hash of the operator's own bytes, and
        // those bytes in the content-addressed store under it.
        Assert.Equal(1, await CountArtifactsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        var evidence = Assert.Single(stored!.Artifacts);

        Assert.Equal(runId, evidence.RunId);
        Assert.Equal("node-a", evidence.StageId);
        Assert.Equal(ArtifactKind, evidence.Kind);
        Assert.Equal(expectedHash, evidence.HashSha256);
        Assert.Equal(bytes.Length, evidence.SizeBytes);
        Assert.True(await host.Services.GetRequiredService<IWorkflowArtifactBlobStore>()
            .VerifyAsync(expectedHash, CancellationToken.None));

        // And the screen still says what happened. The success notice is not downgraded, cleared or turned
        // into a refusal, and none of the writable surfaces carries the failure or the operator's file.
        Assert.Empty(library.Blocker);
        Assert.Contains(runId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("node-a", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(ArtifactKind, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(expectedHash, library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("не прикреплён", library.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("прервана до записи", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("прервана до записи", library.Blocker, StringComparison.Ordinal);
        AssertNoFileDetail(library.RunCommandNotice, path, "failing-close.md", "failing-close");
        AssertNoFileDetail(library.StatusMessage, path, "failing-close.md", "failing-close");
        AssertNoFileDetail(library.Blocker, path, "failing-close.md", "failing-close");
        AssertNoFileDetail(library.ObservedRunDisplay, path, "failing-close.md", "failing-close");
        AssertNoFileDetail(library.StageArtifactRequirementDisplay, path, "failing-close.md", "failing-close");

        // The stage the artifact was attached for is now past its artifact-only gate, which is the other
        // way of saying the artifact really was recorded.
        await library.AdvanceObservedRunAsync();

        Assert.Empty(library.Blocker);
        Assert.Equal("node-b", library.ObservedRun!.CurrentStageId);
    }

    /// <summary>
    /// The shipped control and its bindings, taken from the real <c>ScreenTemplates.xaml</c> the
    /// application loads. The requirement line, the path box, the classification choice and the attach
    /// action are the four things the operator uses, and each of them is asserted against the view model
    /// the run commands actually drive.
    /// </summary>
    [Fact]
    public async Task TheShippedRunConsoleBindsTheArtifactRequirementPathClassificationAndAttachAction()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        StaTestRunner.EnsureApplication();

        Window? window = null;
        FrameworkElement root = null!;

        try
        {
            ShippedArtifactControls state = StaTestRunner.Run(() =>
            {
                var opened = OpenTemplatePanel(library);
                window = opened.Window;
                root = opened.Root;

                var pathBox = (TextBox)FindVisualDescendants<FrameworkElement>(opened.Root)
                    .Single(element => element.Name == "LibraryStageArtifactPathBox");
                var classificationPicker = (ComboBox)FindVisualDescendants<FrameworkElement>(opened.Root)
                    .Single(element => element.Name == "LibraryStageArtifactClassificationPicker");
                var attachButton = (Button)FindVisualDescendants<FrameworkElement>(opened.Root)
                    .Single(element => element.Name == "LibraryAttachStageArtifactButton");

                return new ShippedArtifactControls(
                    BindingPathOf(pathBox, TextBox.TextProperty),
                    BindingPathOf(pathBox, UIElement.IsEnabledProperty),
                    BindingPathOf(classificationPicker, Selector.SelectedItemProperty),
                    BindingPathOf(classificationPicker, UIElement.IsEnabledProperty),
                    BindingPathOf(attachButton, ButtonBase.CommandProperty),
                    attachButton.Content as string ?? string.Empty,
                    attachButton.IsEnabled,
                    classificationPicker.SelectedItem,
                    ReadTextBlocks(opened.Root));
            });

            // The two inputs bind the captured path and classification, and both are frozen by the same
            // busy flag the product pickers use. The action is the view model's own command.
            Assert.Equal("StageArtifactPath", state.PathBinding);
            Assert.Equal("IsStageArtifactInputEditable", state.PathEnabledBinding);
            Assert.Equal("StageArtifactClassification", state.ClassificationBinding);
            Assert.Equal("IsStageArtifactInputEditable", state.ClassificationEnabledBinding);
            Assert.Equal("AttachStageArtifactCommand", state.AttachCommandBinding);
            Assert.Equal("Прикрепить артефакт", state.AttachContent);
            Assert.True(state.AttachEnabled);
            Assert.Equal(DataClassification.PrivateSource, state.Classification);

            // The three classification choices are the real declared values, and the default is the
            // private one rather than the most permissive one.
            Assert.Equal(
                new[] { DataClassification.PublicSource, DataClassification.PrivateSource, DataClassification.Restricted },
                library.StageArtifactClassifications);

            // The requirement line is on screen and carries the run, the stage and the kind the pinned
            // scheme declared, with no operator input chosen yet.
            Assert.Contains(
                state.Texts,
                line => line.Contains(library.ObservedRun!.Id, StringComparison.Ordinal)
                    && line.Contains("node-a", StringComparison.Ordinal)
                    && line.Contains(ArtifactKind, StringComparison.Ordinal));

            // The action says what it does to the operator's file, and the screen offers no hash input
            // next to it: no editable field anywhere in the panel is bound to a hash.
            Assert.Contains(state.Texts, line => line.Contains("адресное хранилище", StringComparison.Ordinal));
            Assert.Empty(StaTestRunner.Run(
                () => FindVisualDescendants<FrameworkElement>(root)
                    .OfType<TextBox>()
                    .Where(textBox => BindingPathOf(textBox, TextBox.TextProperty)
                        .Contains("Hash", StringComparison.Ordinal))
                    .Select(textBox => BindingPathOf(textBox, TextBox.TextProperty))
                    .ToArray()));
        }
        finally
        {
            var opened = window;

            if (opened is not null)
            {
                StaTestRunner.Run(() => opened.Close());
            }
        }
    }

    private static string BindingPathOf(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBindingExpression(element, property)?.ParentBinding.Path.Path ?? string.Empty;

    /// <summary>What the shipped run console actually declares, read off the real loaded XAML.</summary>
    private sealed record ShippedArtifactControls(
        string PathBinding,
        string PathEnabledBinding,
        string ClassificationBinding,
        string ClassificationEnabledBinding,
        string AttachCommandBinding,
        string AttachContent,
        bool AttachEnabled,
        object? Classification,
        string[] Texts);

    /// <summary>
    /// An intentional replacement: the newest bytes become the current artifact, and the verdicts and the
    /// approval that were recorded against the previous hash stop authorizing the transition. Without
    /// this, replacing a file would leave stale human evidence standing over content nobody reviewed.
    ///
    /// The stage used here declares reviewers and a user approval as well as the artifact, so the case
    /// covers the gates that actually exist; a stage with only an artifact requirement has no human
    /// evidence to invalidate and is covered by the unblocking test above.
    /// </summary>
    [Fact]
    public async Task ReplacingTheArtifactMakesTheNewestBytesCurrentAndStopsTheEarlierEvidenceFromAuthorizing()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(
            host.Services,
            ArtifactKind,
            requiredReviewerRoles: new[] { "Reviewer" },
            requiresUserApproval: true);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        var runId = library.ObservedRun!.Id;
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var evidenceTime = DateTimeOffset.UtcNow.AddHours(1);

        var firstBytes = Encoding.UTF8.GetBytes("first reviewed draft");
        var firstHash = ContentHash(firstBytes);
        library.StageArtifactPath = WriteArtifactFile("first-draft.md", firstBytes);
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
        Assert.Equal(firstHash, Assert.Single(library.ObservedRun!.Artifacts).HashSha256);

        // Historical fixture evidence over the first draft, pinned to the stored hash. Execution metadata
        // is not a model response: the fixture first asserts the production verdict API refuses it.
        var firstArtifact = Assert.Single(library.ObservedRun!.Artifacts);
        var firstExecutionId = await PersistReviewerExecutionAsync(
            host.Services,
            runId,
            "Reviewer",
            "node-a",
            firstArtifact,
            evidenceTime);

        await HistoricalWorkflowReviewFixture.SeedAsync(host.Services, runId, new ReviewerVerdictRecord(
            "Reviewer",
            ReviewerRouteId,
            firstHash,
            WorkflowReviewVerdict.Approve,
            "Reviewed the first draft.",
            evidenceTime,
            firstExecutionId,
            "node-a",
            firstArtifact.ArtifactId));

        await runService.RecordUserApprovalAsync(runId, new UserApprovalEvidence(
            Guid.NewGuid().ToString("N"),
            "operator",
            "node-a",
            firstHash,
            UserApprovalDecision.Approved,
            "Approved the first draft.",
            evidenceTime));

        var secondBytes = Encoding.UTF8.GetBytes("second draft nobody has reviewed");
        var secondHash = ContentHash(secondBytes);
        library.StageArtifactPath = WriteArtifactFile("second-draft.md", secondBytes);
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);

        // Both records are kept, so the replacement is visible as history rather than as an overwrite, and
        // the newest one is the current artifact for this run, stage and kind.
        Assert.Equal(2, await CountArtifactsAsync());

        var stored = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);
        var current = WorkflowArtifactEvidence.SelectCurrent(stored!.Artifacts, runId, "node-a", ArtifactKind);

        Assert.NotNull(current);
        Assert.Equal(secondHash, current!.HashSha256);
        Assert.Contains(secondHash, library.StageArtifactRequirementDisplay, StringComparison.Ordinal);

        // The evidence recorded over the first draft authorizes nothing now: the transition is refused
        // even though the reviewer approved and the user approved, because both pinned the previous hash.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(runId, "gate probe after the replacement"));

        Assert.Contains(secondHash, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstHash, refusal.Message, StringComparison.Ordinal);

        // The screen's own advance command reports the same refusal rather than pretending to move.
        await library.AdvanceObservedRunAsync();

        Assert.False(string.IsNullOrWhiteSpace(library.Blocker));
        Assert.Equal("node-a", library.ObservedRun!.CurrentStageId);

        // New historical claims on the replacement hash satisfy aggregate metadata checks only. The
        // product still refuses advancement because persisted parsed response evidence is absent.
        var secondArtifact = await CurrentArtifactAsync(host.Services, runId);
        var secondExecutionId = await PersistReviewerExecutionAsync(
            host.Services,
            runId,
            "Reviewer",
            "node-a",
            secondArtifact,
            evidenceTime.AddMinutes(1));

        await HistoricalWorkflowReviewFixture.SeedAsync(host.Services, runId, new ReviewerVerdictRecord(
            "Reviewer",
            ReviewerRouteId,
            secondHash,
            WorkflowReviewVerdict.Approve,
            "Reviewed the second draft.",
            evidenceTime.AddMinutes(1),
            secondExecutionId,
            "node-a",
            secondArtifact.ArtifactId));

        await runService.RecordUserApprovalAsync(runId, new UserApprovalEvidence(
            Guid.NewGuid().ToString("N"),
            "operator",
            "node-a",
            secondHash,
            UserApprovalDecision.Approved,
            "Approved the second draft.",
            evidenceTime.AddMinutes(1)));

        await library.AdvanceObservedRunAsync();

        Assert.Contains("model response", library.Blocker, StringComparison.Ordinal);
        Assert.Equal("node-a", library.ObservedRun!.CurrentStageId);
    }

    /// <summary>
    /// A one-shot suspension point inside the run service. It puts the test exactly where the screen
    /// cannot see the outcome yet: after the command has committed its identities, before it learns that
    /// the service succeeded.
    /// </summary>
    private sealed class RunServiceGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        /// <summary>Suspends exactly the next start, advance or artifact attach; later calls pass straight through.</summary>
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
    /// A suspension point inside the run repository's active-run read. It puts the test exactly where the
    /// screen's enablement is decided from a selection nobody has observed yet: after the operator has
    /// changed the selection, before the screen learns whether that project has an open run.
    /// <para>
    /// While it is armed every active-run read is held, not just the first one, because a selection change
    /// legitimately starts more than one observation of the same project and the window under test has to
    /// survive all of them. Without it, "immediately after the setter" would be a race the test cannot win
    /// honestly - a reload that happened to finish first would make the assertion true for the wrong reason.
    /// </para>
    /// </summary>
    private sealed class RunObservationGate
    {
        private readonly object _sync = new();
        private Hold? _current;

        /// <summary>Completes as soon as any active-run read has reached the armed gate.</summary>
        public Task Entered
        {
            get
            {
                lock (_sync)
                {
                    return _current?.Entered ?? Task.CompletedTask;
                }
            }
        }

        /// <summary>Holds every following active-run read until the gate is opened.</summary>
        public void Arm()
        {
            lock (_sync)
            {
                _current = new Hold();
            }
        }

        /// <summary>Releases every held read; later reads pass straight through.</summary>
        public void Open()
        {
            Hold? current;

            lock (_sync)
            {
                current = _current;
                _current = null;
            }

            current?.Release();
        }

        public Task WaitAsync()
        {
            Hold? current;

            lock (_sync)
            {
                current = _current;
            }

            return current is null ? Task.CompletedTask : current.WaitAsync();
        }

        private sealed class Hold
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public Task WaitAsync()
            {
                _entered.TrySetResult();
                return _release.Task;
            }

            public void Release() => _release.TrySetResult();
        }
    }

    /// <summary>
    /// The product run repository with only its active-run read suspended at the gate. Every other read and
    /// every stored row is the real one, so what the screen is asked to decide is a fact about the database.
    /// </summary>
    private sealed class GatedWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly IWorkflowRunRepository _inner;
        private readonly RunObservationGate _gate;

        public GatedWorkflowRunRepository(IWorkflowRunRepository inner, RunObservationGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner
                .GetActiveByProjectIdAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(run, cancellationToken);

        public Task SaveArtifactAsync(
            WorkflowRun run,
            WorkflowArtifactEvidence evidence,
            CancellationToken cancellationToken = default) =>
            _inner.SaveArtifactAsync(run, evidence, cancellationToken);

        public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            _inner.GetByProjectIdAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// The product run service with only its start, advance and artifact attach suspended at the gate.
    /// Every other command, and the whole run each one produces, is the real one.
    /// </summary>
    /// <summary>
    /// The operator's file behind a handle that closes badly. Reads are the real ones - the run service
    /// still streams the operator's own bytes into the blob store and computes the real hash - and only
    /// the close is broken: the wrapped window is released first, and then the close itself raises.
    ///
    /// That is the only shape of post-commit fault the attach path has, and it is the one a refusal there
    /// would be a lie about: the artifact is already stored by the time this throws.
    /// </summary>
    private sealed class FaultingCloseStream
    {
        private Stream? _inner;

        /// <summary>Whether the wrapped window was really released before the close failed.</summary>
        public bool InnerClosed { get; private set; }

        public Stream Wrap(Stream content)
        {
            _inner = content;
            return new FaultingCloseWindow(this, content);
        }

        private void Release()
        {
            if (_inner is null)
            {
                return;
            }

            var inner = _inner;
            _inner = null;
            inner.Dispose();
            InnerClosed = true;

            // The file is closed; only the close reported a failure, which is a fact about the operator's
            // file and not a statement about the artifact the run service already committed.
            throw new IOException("The artifact file handle could not be released.");
        }

        private sealed class FaultingCloseWindow : Stream
        {
            private readonly FaultingCloseStream _owner;
            private readonly Stream _inner;

            public FaultingCloseWindow(FaultingCloseStream owner, Stream inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.Read(buffer, offset, count);

            public override int Read(Span<byte> buffer) => _inner.Read(buffer);

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);

            public override int ReadByte() => _inner.ReadByte();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

            protected override void Dispose(bool disposing) => _owner.Release();

            public override ValueTask DisposeAsync()
            {
                _owner.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class GatedWorkflowRunService : IWorkflowRunService
    {
        private readonly IWorkflowRunService _inner;
        private readonly RunServiceGate _gate;

        public GatedWorkflowRunService(IWorkflowRunService inner, RunServiceGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task<WorkflowRun> StartRunAsync(
            string projectId,
            string workflowPackageId,
            string workflowVersionId,
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner
                .StartRunAsync(projectId, workflowPackageId, workflowVersionId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<WorkflowRun> AdvanceStageAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner.AdvanceStageAsync(runId, reason, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WorkflowRun> RecordStageArtifactAsync(
            string runId,
            string stageId,
            string kind,
            Stream content,
            DataClassification classification,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner
                .RecordStageArtifactAsync(runId, stageId, kind, content, classification, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<WorkflowRun> AdvanceToDeclaredFailureStageAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.AdvanceToDeclaredFailureStageAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> StartLegacyRunAsync(
            string projectId,
            string workflowPackageId,
            string workflowVersionId,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            _inner.StartLegacyRunAsync(projectId, workflowPackageId, workflowVersionId, sessionId, cancellationToken);

        public Task<WorkflowRun> RecordReviewerVerdictAsync(
            string runId,
            ReviewerVerdictRecord verdict,
            CancellationToken cancellationToken = default) =>
            _inner.RecordReviewerVerdictAsync(runId, verdict, cancellationToken);

        public Task<WorkflowRun> RecordLegacyUnlinkedReviewerVerdictAsync(
            string runId,
            ReviewerVerdictRecord verdict,
            CancellationToken cancellationToken = default) =>
            _inner.RecordLegacyUnlinkedReviewerVerdictAsync(runId, verdict, cancellationToken);

        public Task<WorkflowRun> RecordUserApprovalAsync(
            string runId,
            UserApprovalEvidence approval,
            CancellationToken cancellationToken = default) =>
            _inner.RecordUserApprovalAsync(runId, approval, cancellationToken);

        public Task<WorkflowRun> CancelRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.CancelRunAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> FailRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.FailRunAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> CompleteRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.CompleteRunAsync(runId, reason, cancellationToken);
    }

    private async Task SeedAsync(IServiceProvider services)
    {
        // A real imported ZIP blob, so the shipped tree/Markdown preview of the selected version resolves
        // from storage and the screen reports no unrelated blocker while the run seam is under test.
        var blobStore = services.GetRequiredService<WorkflowBlobStore>();
        await using (var content = new MemoryStream(CreateImportedWorkflowZip()))
        {
            await blobStore.SaveBlobAsync(content);
        }

        var blobId = WorkflowBlobStore.ComputeBlobId(CreateImportedWorkflowZip());

        await ExecuteAsync(
            """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($project, 'Run product project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'ZipArchive', $blob, $blob, $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version1, $package, 1, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version2, $package, 2, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowBindings (Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('2f0a1b6c-1f4e-4a3b-9a7d-5c2b8e0d1a34', $project, $package, $version2, NULL, $now, $now);
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($profile, 'Run product profile', 'OpenCode', 'PrivateSource', $now, $now);
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($account, $profile, 'Run product account', 'Unverified', 'Unknown', $now, $now);
            INSERT INTO Models (
                Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                Health, DiscoveredAtUtc)
            VALUES ($model, 'OpenCode', $profile, 'run-product-model', 'Run product model', 'Unknown', 'Imported',
                    'Unknown', $now);
            INSERT INTO Routes (
                Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health,
                CreatedAtUtc, UpdatedAtUtc)
            VALUES ($route, 'OpenCode', $profile, $account, $model, 'PrivateSource', 'Healthy', $now, $now);
            """,
            ("$project", ProjectId),
            ("$package", PackageId),
            ("$version1", VersionOneId),
            ("$version2", VersionTwoId),
            ("$blob", blobId),
            ("$profile", ReviewerProfileId),
            ("$account", ReviewerAccountId),
            ("$model", ReviewerModelId),
            ("$route", ReviewerRouteId),
            ("$now", "2026-09-29T00:00:00Z"));
    }

    /// <summary>
    /// The run-scoped reviewer execution a product verdict is read out of: a real session bound to the seeded
    /// route's own account, profile and model, a real execution whose requested and observed routes are that
    /// stored <c>Routes</c> row, and a durable binding of it to this run, stage, role and artifact row.
    /// <para>
    /// A verdict the run service would accept has to be backed by one of these, which is the point of the
    /// change under test: a role, a route string and a hash typed by a caller are no longer enough.
    /// </para>
    /// </summary>
    private static async Task<string> PersistReviewerExecutionAsync(
        IServiceProvider services,
        string runId,
        string role,
        string stageId,
        WorkflowArtifactEvidence artifact,
        DateTimeOffset recordedAtUtc)
    {
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

        return executionId;
    }

    /// <summary>
    /// The run's newest stored artifact of the test's own kind at its own stage, read from storage rather
    /// than from the screen, so an execution is always bound to a row that really exists.
    /// </summary>
    private static async Task<WorkflowArtifactEvidence> CurrentArtifactAsync(
        IServiceProvider services,
        string runId)
    {
        var stored = await services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

        return WorkflowArtifactEvidence.SelectCurrent(
                   stored!.Artifacts,
                   runId,
                   stored.CurrentStageId,
                   ArtifactKind)
               ?? throw new InvalidOperationException(
                   $"Run '{runId}' is expected to have a stored '{ArtifactKind}' artifact at stage "
                   + $"'{stored.CurrentStageId}'.");
    }

    private static byte[] CreateImportedWorkflowZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var readme = archive.CreateEntry("README.md");
            using var writer = new StreamWriter(readme.Open());
            writer.Write("Imported workflow package.");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The real production composition over one migrated database in a throwaway app-data directory: the
    /// same graph <c>App.OnStartup</c> builds, including the registered run service, the registered
    /// repositories and the registered screen. Nothing is hand-wired here, so a green assertion is evidence
    /// about what actually ships.
    /// </summary>
private IHost CreateHost(RunServiceGate? gate = null, RunObservationGate? observationGate = null) =>
        HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();

                if (gate is not null)
                {
                    SuspendRunServiceAt(services, gate);
                }

                if (observationGate is not null)
                {
                    SuspendRunObservationAt(services, observationGate);
                }
            })
            .Build();

    /// <summary>
    /// Wraps the product's own <see cref="IWorkflowRunRepository"/> registration in a gate instead of
    /// replacing it, so a test can hold a run observation inside the real repository. The rows it reads are
    /// the stored ones; only the moment the read returns moves.
    /// </summary>
    private static void SuspendRunObservationAt(IServiceCollection services, RunObservationGate gate)
    {
        var product = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IWorkflowRunRepository))
            ?? throw new InvalidOperationException("The product composition no longer registers IWorkflowRunRepository.");

        var productFactory = ResolveImplementationFactory(product);

        services.Remove(product);
        services.AddSingleton<IWorkflowRunRepository>(provider => new GatedWorkflowRunRepository(
            (IWorkflowRunRepository)productFactory(provider),
            gate));
    }

    /// <summary>
    /// The factory that produces the product's own registration, whether the product declared it as one or
    /// as an implementation type the container has to construct.
    /// </summary>
    private static Func<IServiceProvider, object> ResolveImplementationFactory(ServiceDescriptor descriptor) =>
        descriptor.ImplementationFactory
        ?? (descriptor.ImplementationType is { } implementationType
            ? provider => ActivatorUtilities.CreateInstance(provider, implementationType)
            : throw new InvalidOperationException(
                "The workflow run repository is expected to be registered through its own factory or type."));

    /// <summary>
    /// Wraps the product's own <see cref="IWorkflowRunService"/> registration in a gate instead of
    /// replacing it, so a test can hold a run command inside the real service. Nothing about pinning,
    /// persistence or the transition itself is faked - only the moment the call returns moves.
    /// </summary>
    private static void SuspendRunServiceAt(IServiceCollection services, RunServiceGate gate)
    {
        var product = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IWorkflowRunService))
            ?? throw new InvalidOperationException("The product composition no longer registers IWorkflowRunService.");

        var productFactory = product.ImplementationFactory
            ?? throw new InvalidOperationException(
                "IWorkflowRunService is expected to be registered through its own factory.");

        services.Remove(product);
        services.AddSingleton<IWorkflowRunService>(provider => new GatedWorkflowRunService(
            (IWorkflowRunService)productFactory(provider),
            gate));
    }

    private async Task<IHost> CreateInitializedHostAsync(
        RunServiceGate? gate = null,
        RunObservationGate? observationGate = null)
    {
        var host = CreateHost(gate, observationGate);
        await HostBootstrapper.InitializeAsync(host);
        return host;
    }

    /// <summary>The screen the shipped Workflows screen and the Workflow Console both drive.</summary>
    private static WorkflowLibraryViewModel CreateLibrary(IServiceProvider services) =>
        services.GetRequiredService<WorkflowLibraryViewModel>();

    private static async Task AssignLinearTemplateAsync(
        IServiceProvider services,
        int version,
        string[] nodeIds)
    {
        var store = services.GetRequiredService<IWorkflowTemplateStore>();

        await store.SaveAsync(CreateLinearTemplate(TemplateId, version, nodeIds));
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"assignment-{version}",
            ProjectId,
            TemplateId,
            version,
            SeededAt));
    }

    private static WorkflowTemplateDefinition CreateLinearTemplate(
        string templateId,
        int version,
        string[] nodeIds)
    {
        var nodes = new List<WorkflowNodeDefinition>(nodeIds.Length);

        for (var index = 0; index < nodeIds.Length; index++)
        {
            var isLast = index == nodeIds.Length - 1;

            nodes.Add(isLast
                ? Terminal(nodeIds[index])
                : Prompt(nodeIds[index], success: nodeIds[index + 1]));
        }

        return new WorkflowTemplateDefinition(
            templateId,
            version,
            $"Linear template v{version}",
            "A gate-free linear template.",
            new WorkflowGraph(nodeIds[0], nodes),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            SeededAt);
    }

    private static WorkflowNodeDefinition Prompt(string nodeId, string? success = null) =>
        new(
            nodeId,
            WorkflowNodeKind.Prompt,
            $"Node {nodeId}",
            $"Role {nodeId}",
            successTargetNodeId: success);

    private static WorkflowNodeDefinition Terminal(string nodeId) =>
        new(nodeId, WorkflowNodeKind.TerminalOutcome, $"Node {nodeId}", $"Role {nodeId}");

    /// <summary>
    /// Assigns a template whose entry stage declares an artifact requirement of its own.
    ///
    /// The requirement travels in the node's own gate metadata, which is the only way a pinned stage can
    /// come to require bytes, so the kind the screen later reports is the kind the graph really declared
    /// and not something the test supplied at attach time.
    /// </summary>
    private static async Task AssignArtifactTemplateAsync(
        IServiceProvider services,
        string artifactKind,
        IReadOnlyList<string>? requiredReviewerRoles = null,
        bool requiresUserApproval = false,
        IReadOnlyList<RoleBindingDefinition>? roleBindings = null)
    {
        var store = services.GetRequiredService<IWorkflowTemplateStore>();

        await store.SaveAsync(CreateArtifactTemplate(
            TemplateId,
            version: 1,
            artifactKind,
            requiredReviewerRoles,
            requiresUserApproval,
            roleBindings));

        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-1",
            ProjectId,
            TemplateId,
            1,
            SeededAt));
    }

    private static WorkflowTemplateDefinition CreateArtifactTemplate(
        string templateId,
        int version,
        string artifactKind,
        IReadOnlyList<string>? requiredReviewerRoles,
        bool requiresUserApproval,
        IReadOnlyList<RoleBindingDefinition>? roleBindings = null)
    {
        var reviewers = requiredReviewerRoles ?? Array.Empty<string>();
        var declaresAHumanGate = reviewers.Count > 0 || requiresUserApproval;

        var nodes = new List<WorkflowNodeDefinition>
        {
            Gated(
                "node-a",
                "node-b",
                artifactKind,
                declaresAHumanGate ? WorkflowStageKind.DocumentReview : WorkflowStageKind.Implementation,
                reviewers,
                requiresUserApproval),
            Prompt("node-b", success: "node-c"),
            Terminal("node-c")
        };

        return new WorkflowTemplateDefinition(
            templateId,
            version,
            $"Artifact template v{version}",
            "A linear template whose entry stage requires an artifact.",
            new WorkflowGraph("node-a", nodes),
            roleBindings ?? Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            SeededAt);
    }

    private static WorkflowNodeDefinition Gated(
        string nodeId,
        string success,
        string artifactKind,
        WorkflowStageKind stageKind,
        IReadOnlyList<string> requiredReviewerRoles,
        bool requiresUserApproval) =>
        new(
            nodeId,
            WorkflowNodeKind.Prompt,
            $"Node {nodeId}",
            $"Role {nodeId}",
            successTargetNodeId: success,
            gateMetadata: new WorkflowNodeGateMetadata(
                stageKind,
                requiredReviewerRoles,
                requiresUserApproval,
                artifactKind));

    /// <summary>
    /// Writes a real file under the test's own throwaway root and returns its path. The name is distinctive
    /// on purpose, so the redaction assertions can look for it in a message by accident.
    /// </summary>
    private string WriteArtifactFile(string fileName, byte[] content)
    {
        var directory = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, content);

        return path;
    }

    /// <summary>
    /// The content address the run service computes for these bytes, derived here independently so the
    /// screen's reported hash is compared against the file rather than against itself.
    /// </summary>
    private static string ContentHash(byte[] content) =>
        WorkflowArtifactEvidence.ContentHashPrefix
        + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// The artifact kind the run's own pinned scheme snapshot declares for one stage, read straight from
    /// the stored snapshot. The screen is compared against this rather than against a constant, so the
    /// assertion survives any change to the test template.
    /// </summary>
    private static string? ReadPinnedArtifactKind(WorkflowRun run, string stageId) =>
        WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, run.Id)
            .Scheme
            .FindStage(stageId)?
            .ArtifactRequirement;

    /// <summary>
    /// A refusal has to be named, and it has to be named without the operator's file. Every surface the
    /// screen writes to is checked, because each of them is a durable record, and the temporary root the
    /// test wrote into is checked too: it is the one substring no honest message could legitimately need.
    /// </summary>
    private void AssertNamesNoFileDetail(string message, params string[] forbidden)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(message),
            "A refusal has to be named, not silent.");

        AssertNoFileDetail(message, forbidden);
    }

    /// <summary>
    /// The same redaction check without requiring the surface to have said anything. A refused command
    /// clears the status line on purpose, so only the blocker and the notice are required to be named.
    /// </summary>
    private void AssertNoFileDetail(string message, params string[] forbidden)
    {
        Assert.DoesNotContain(_root, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), message, StringComparison.OrdinalIgnoreCase);

        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<long> CountRunsAsync() => await ReadCountAsync("SELECT COUNT(*) FROM WorkflowRuns;");

    private async Task<long> CountArtifactsAsync() => await ReadCountAsync("SELECT COUNT(*) FROM Artifacts;");

    /// <summary>
    /// The committed blobs on disk, counted as files rather than as rows. A refusal that had already
    /// streamed the operator's file into the store would leave one behind even with no evidence row, and
    /// that is still a refusal that touched the operator's bytes.
    /// </summary>
    private long CountBlobs()
    {
        var directory = Path.Combine(_appData, "blobs", "sha256");

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        // The store's staging directory holds nothing at rest, and counting it would make the assertion
        // about a leak rather than about committed content.
        return Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .Count(path => !path.Contains($"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private async Task<long> ReadCountAsync(string sql)
    {
        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static (Window Window, FrameworkElement Root) OpenTemplatePanel(WorkflowLibraryViewModel viewModel)
    {
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 1280,
            Height = 760,
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

        window.Show();

        host.Measure(new Size(1280, 760));
        host.Arrange(new Rect(0, 0, 1280, 760));
        host.UpdateLayout();

        return (window, host);
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
}
