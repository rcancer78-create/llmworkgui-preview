using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// The path the visible acceptance harness walks, pinned to a really composed run.
/// <para>
/// The existing product-command tests cover the first stage of the built-in template and cover the reviewer
/// gate on a custom template. What is pinned here is the whole document chain of the shipped
/// <c>workflow-standard-development@1</c> walked through the <see cref="WorkflowLibraryViewModel"/> the
/// Workflows screen and the Workflow Console both drive, with real local file bytes at every stage, up to
/// the point where the reviewer gate refuses it - and the proof that refusing it cost the run nothing: the
/// registered production <see cref="IWorkflowReviewEvidenceRepository"/> stays empty, and no session,
/// execution or verdict is invented to get there.
/// </para>
/// </summary>
public sealed class VisibleWorkflowAcceptanceWalkTests : IDisposable
{
    private const string ProjectId = "visible-acceptance-project";
    private const string PackageId = "visible-acceptance-package";
    private const string VersionId = "visible-acceptance-version";

    private static readonly DateTimeOffset SeededAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "llmworkgui-phase10-visible-" + Guid.NewGuid().ToString("N"));

    private readonly string _appData;

    public VisibleWorkflowAcceptanceWalkTests()
    {
        _appData = Path.Combine(_root, "appdata");

        using var host = CreateHost();
        HostBootstrapper.InitializeAsync(host).GetAwaiter().GetResult();
        SeedAsync(host.Services).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must never turn an acceptance assertion red.
            }
        }
    }

    /// <summary>
    /// The whole document chain of the shipped built-in template, driven only through the shipped screen,
    /// ends on a named reviewer-gate refusal with a current <c>DocumentBundle</c> - and the refusal costs
    /// the run nothing, because no reviewer execution, session, execution or verdict is ever written.
    /// </summary>
    [Fact]
    public async Task TheShippedChainWalksToDocumentReviewAndTheReviewerGateRefusesWithoutInventingEvidence()
    {
        using var host = CreateHost();
        await HostBootstrapper.InitializeAsync(host);

        var library = host.Services.GetRequiredService<WorkflowLibraryViewModel>();
        await library.RefreshAsync();

        Assert.Equal(ProjectId, library.SelectedProject!.Id);
        Assert.True(library.CanStartAssignedRun);

        await library.StartAssignedRunAsync();

        var run = library.ObservedRun!;
        Assert.Equal(WorkflowStudioService.StandardTemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);

        // Every stage before the review gate declares exactly one stored document and no reviewer.
        var documentStages = new (string StageId, string Kind)[]
        {
            (WorkflowScheme.TaskSpecificationStageId, "TaskSpecificationDocument"),
            (WorkflowScheme.ArchitectureStageId, "ArchitectureDocument"),
            (WorkflowScheme.TechnicalSpecificationStageId, "TechnicalSpecificationDocument"),
            (WorkflowScheme.RoadmapStageId, "RoadmapDocument"),
            (WorkflowScheme.DocumentReviewStageId, "DocumentBundle")
        };

        foreach (var (stageId, kind) in documentStages)
        {
            var bytes = Encoding.UTF8.GetBytes($"# {kind} for {stageId} in the visible acceptance walk.");
            var path = Path.Combine(_root, kind + ".md");
            await File.WriteAllBytesAsync(path, bytes);

            library.StageArtifactPath = path;

            Assert.True(library.CanAttachStageArtifact, $"attach is unoffered on {stageId}");
            await library.AttachStageArtifactAsync();

            Assert.Contains(
                "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                library.StageArtifactRequirementDisplay,
                StringComparison.Ordinal);

            if (stageId == WorkflowScheme.DocumentReviewStageId)
            {
                break;
            }

            Assert.True(library.CanAdvanceObservedRun, $"advance is unoffered after {stageId}");
            await library.AdvanceObservedRunAsync();
        }

        Assert.Equal(WorkflowScheme.DocumentReviewStageId, library.ObservedRun!.CurrentStageId);

        // The gate is evaluated against the hash of the bytes that were actually committed.
        var gate = library.ReviewGateStatus;
        Assert.Equal(WorkflowReviewGateAvailability.Evaluated, gate.Availability);
        Assert.Equal("DocumentBundle", gate.RequiredArtifactKind);
        Assert.True(gate.ArtifactBytesVerified);
        Assert.Equal(new[] { "Reviewer", "Architect" }, gate.Roles.Select(role => role.Role).ToArray());
        Assert.All(gate.Roles, role => Assert.Equal(WorkflowReviewGateRoleState.Missing, role.State));

        // Advance is refused by the gate with the stored hash named, and the run does not move.
        await library.AdvanceObservedRunAsync();

        Assert.Contains("verdict", library.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, library.ObservedRun!.CurrentStageId);

        // The assigned review may still be asked for, and its named refusal persists nothing at all.
        Assert.True(library.CanRequestAssignedReview);
        await library.RequestAssignedReviewAsync();

        Assert.False(string.IsNullOrWhiteSpace(library.AssignedReviewStatusDisplay));

        var stored = (await host.Services.GetRequiredService<IWorkflowRunRepository>()
            .GetByIdAsync(library.ObservedRun.Id))!;

        Assert.Empty(stored.Verdicts);
        Assert.Empty(stored.Approvals);
        Assert.Equal(4, stored.Transitions.Count);
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, stored.CurrentStageId);

        Assert.Empty(await host.Services.GetRequiredService<IWorkflowReviewEvidenceRepository>()
            .ListByRunIdAsync(stored.Id));

        var sessions = await host.Services.GetRequiredService<ISessionRepository>().ListByProjectAsync(ProjectId);
        Assert.DoesNotContain(sessions, session => session.WorkflowRunId == stored.Id);

        // Only the production SQLite repository is registered; no test reviewer store stood in for it.
        Assert.IsType<SqliteWorkflowReviewEvidenceRepository>(
            host.Services.GetRequiredService<IWorkflowReviewEvidenceRepository>());
    }

    /// <summary>
    /// The composition the visible harness depends on: the storage root and the shell's layout memory both
    /// resolve inside the temporary application-data root with nothing registered for the layout at all, so
    /// an acceptance run cannot read or overwrite the real user profile.
    /// <para>
    /// The harness used to pre-register a <see cref="LayoutPersistenceService"/> before
    /// <c>AddUnifiedWorkspaceShell</c> to work around a product defect. Production now binds the layout
    /// store to the configured root itself, so this host is composed exactly like <c>App.OnStartup</c>
    /// composes it and the path still lands inside the temporary root.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AVisibleAcceptanceHostKeepsTheRealUserProfileClosed()
    {
        var layoutFile = Path.Combine(_appData, LayoutPersistenceService.DefaultFileName);

        using var host = HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();
            })
            .Build();

        await HostBootstrapper.InitializeAsync(host);

        var storage = host.Services.GetRequiredService<StorageOptions>();

        Assert.Equal(Path.GetFullPath(_appData), storage.AppDataDirectory);

        var persistence = Assert.IsType<LayoutPersistenceService>(
            host.Services.GetRequiredService<ILayoutPersistenceService>());

        Assert.Equal(Path.GetFullPath(layoutFile), persistence.FilePath);
        Assert.StartsWith(
            Path.GetFullPath(_appData),
            persistence.FilePath,
            StringComparison.OrdinalIgnoreCase);

        // A save and a load of the composed store really happen inside the temporary root, so the
        // isolation claim covers the whole store and not only the path it resolves to.
        persistence.Save(new ShellLayoutState { LeftPaneWidth = 210, ActiveScreen = "Workflows" });

        Assert.True(File.Exists(layoutFile));
        Assert.Equal(210, persistence.Load().LeftPaneWidth);
        Assert.Equal("Workflows", persistence.Load().ActiveScreen);

        // The production App is never started by the harness, so no real user profile is opened here.
        Assert.NotNull(host.Services.GetRequiredService<UnifiedWorkspaceShellViewModel>());
        Assert.NotNull(host.Services.GetRequiredService<WorkflowLibraryViewModel>());
    }

    private IHost CreateHost() =>
        HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();
            })
            .Build();

    private async Task SeedAsync(IServiceProvider services)
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));

        var blobStore = services.GetRequiredService<WorkflowBlobStore>();
        var zip = CreatePackageZip();
        var blobId = WorkflowBlobStore.ComputeBlobId(zip);

        await using (var content = new MemoryStream(zip, writable: false))
        {
            await blobStore.SaveBlobAsync(content);
        }

        await services.GetRequiredService<IProjectRepository>().UpsertAsync(new Project(
            ProjectId,
            "Visible acceptance project",
            Path.Combine(_root, "workspace"),
            gitBranch: null,
            isDirty: false,
            hasRequiredInstructions: false,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource));

        await services.GetRequiredService<IWorkflowPackageRepository>().UpsertAsync(new WorkflowPackage(
            PackageId,
            "visible-acceptance.zip",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            blobId,
            blobId,
            SeededAt,
            SeededAt));

        await services.GetRequiredService<IWorkflowVersionRepository>().UpsertAsync(new WorkflowVersion(
            VersionId,
            PackageId,
            versionNumber: 1,
            blobId: blobId,
            originalHash: blobId,
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: null,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            createdAtUtc: SeededAt,
            activatedAtUtc: null));

        await services.GetRequiredService<IWorkflowBindingRepository>().UpsertAsync(new WorkflowBinding(
            Guid.NewGuid().ToString(),
            ProjectId,
            PackageId,
            VersionId,
            routePolicyId: null,
            SeededAt,
            SeededAt));

        // Template selection alone is not assignment: without this the shipped start command refuses with
        // no-template-assignment.
        var assignment = await services.GetRequiredService<IWorkflowStudioService>().AssignTemplateToProjectAsync(
            ProjectId,
            WorkflowStudioService.StandardTemplateId,
            templateVersion: 1);

        Assert.True(assignment.IsAssigned);
    }

    private static byte[] CreatePackageZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("README.md").Open());
            writer.Write("# Visible acceptance package");
        }

        return buffer.ToArray();
    }
}
