using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the workflow library screen. The view model is exercised over in-memory
/// repositories and the real <see cref="WorkflowBindingService"/>, so the assertions are about the
/// immutability contract and the active-version pointer the operator actually sees.
/// </summary>
public sealed partial class WorkflowLibraryViewModelTests
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);
    private static readonly string HashC = "sha256:" + new string('c', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionRepositoryFailure_IsObservedAndDoesNotPublishPrivateDiagnostics()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-failure", HashA);
        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        library.Versions.ListFailure = new InvalidOperationException("token=synthetic-private-versions");
        Assert.Null(await Record.ExceptionAsync(viewModel.LoadVersionsAsync));
        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("synthetic-private-versions", viewModel.Blocker);
        Assert.Empty(viewModel.Versions);
    }

    [Fact]
    public void WithoutServices_TheScreenStatesTheLibraryIsUnavailable()
    {
        var viewModel = new WorkflowLibraryViewModel();

        Assert.False(viewModel.IsLibraryAvailable);
        Assert.False(viewModel.IsPreviewAvailable);
        Assert.False(viewModel.IsBindingAvailable);
        Assert.False(viewModel.IsImportAvailable);
        Assert.False(viewModel.IsExportAvailable);
        Assert.False(viewModel.IsProjectSelectionAvailable);
        Assert.Equal(viewModel.LibraryUnavailableNotice, viewModel.EmptyStateMessage);
        Assert.Contains("неизменяем", viewModel.LibraryNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutServices_RefreshAndPreviewDoNothingAndReportNoBlocker()
    {
        var viewModel = new WorkflowLibraryViewModel();

        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();
        await viewModel.LoadFilePreviewAsync();

        Assert.Empty(viewModel.Packages);
        Assert.Empty(viewModel.Versions);
        Assert.Empty(viewModel.TreeNodes);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public async Task Refresh_LoadsPackagesAndProjects()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.True(viewModel.HasPackages);
        Assert.Single(viewModel.Packages);
        Assert.Equal("pkg-1", viewModel.Packages[0].Id);
        Assert.True(viewModel.HasProjects);
        Assert.Equal("project-1", viewModel.SelectedProject!.Id);
        Assert.Equal("Project project-1", viewModel.SelectedProjectDisplay);
    }

    [Fact]
    public async Task Refresh_WithoutAProjectRepository_DoesNotInventAProject()
    {
        var library = CreateLibrary(withProjects: false);
        library.AddPackage("pkg-1", HashA);

        var viewModel = library.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsProjectSelectionAvailable);
        Assert.False(viewModel.HasProjects);
        Assert.Null(viewModel.SelectedProject);
        Assert.Equal("Проект не выбран", viewModel.SelectedProjectDisplay);
    }

    [Fact]
    public async Task SelectingPackage_LoadsItsVersions()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        await viewModel.LoadVersionsAsync();

        Assert.True(viewModel.HasVersions);
        Assert.Equal(2, viewModel.Versions.Count);
        Assert.Equal(new[] { "v1", "v2" }, viewModel.Versions.Select(version => version.VersionDisplay).ToArray());
        Assert.Equal("ver-1", viewModel.SelectedVersion!.Id);
    }

    [Fact]
    public async Task SelectingVersion_LoadsThePreviewStrictlyByTheVersionBlobId()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashB);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashB,
            new[] { new WorkflowTreeNode("README.md", "README.md", false, 10, 5, Now) },
            "README.md",
            "# Immutable workflow",
            IsPrimaryDocumentationTruncated: false);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();

        await viewModel.LoadPreviewAsync();

        // The preview must come from version.BlobId (HashB), never from package.OriginalBlobId (HashA).
        Assert.NotEmpty(library.Preview.TreeRequests);
        Assert.All(library.Preview.TreeRequests, blobId => Assert.Equal(HashB, blobId));
        Assert.DoesNotContain(HashA, library.Preview.TreeRequests);
        Assert.Equal("README.md", viewModel.DocumentationPath);
        Assert.Equal("# Immutable workflow", viewModel.DocumentationContent);
        Assert.True(viewModel.HasDocumentation);
    }

    [Fact]
    public async Task DocumentationTruncation_IsSurfacedFromThePrimaryDocumentationFlag()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashA,
            Array.Empty<WorkflowTreeNode>(),
            "README.md",
            "truncated",
            IsPrimaryDocumentationTruncated: true);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        Assert.True(viewModel.IsDocumentationTruncated);
        Assert.Contains("512 КиБ", viewModel.DocumentationTruncationNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tree_IsGroupedByPathAndFlattenedWithIndentation()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashA,
            new[]
            {
                new WorkflowTreeNode("src/app.cs", "app.cs", false, 120, 60, Now),
                new WorkflowTreeNode("src", "src", true, 120, 60, Now),
                new WorkflowTreeNode("README.md", "README.md", false, 30, 20, Now)
            },
            "README.md",
            "# Readme",
            false);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        Assert.True(viewModel.HasTreeNodes);
        Assert.Equal(
            new[] { "README.md", "src", "app.cs" },
            viewModel.TreeNodes.Select(node => node.Name).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, viewModel.TreeNodes.Select(node => node.Depth).ToArray());

        var directory = viewModel.TreeNodes.Single(node => node.IsDirectory);
        var child = viewModel.TreeNodes.Single(node => node.Name == "app.cs");

        Assert.True(child.Indent.Left > directory.Indent.Left);
        Assert.Equal("Directory", directory.KindDisplay);
        Assert.Equal("File", child.KindDisplay);
    }

    [Fact]
    public async Task SelectingFileNode_LoadsTheFilePreviewByTheVersionBlobId()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashB);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashB,
            new[] { new WorkflowTreeNode("src/app.cs", "app.cs", false, 120, 60, Now) },
            null,
            null,
            false);

        library.Preview.FilePreview = new WorkflowFilePreview(HashB, "src/app.cs", 120, false, false, "class App {}");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        viewModel.SelectedNode = viewModel.TreeNodes.Single();

        await viewModel.LoadFilePreviewAsync();

        Assert.NotEmpty(library.Preview.FileRequests);
        Assert.All(
            library.Preview.FileRequests,
            request => Assert.Equal((HashB, "src/app.cs"), request));
        Assert.Equal("class App {}", viewModel.SelectedFileContent);
        Assert.True(viewModel.HasSelectedFileContent);
        Assert.False(viewModel.IsSelectedFileBinary);
    }

    [Fact]
    public async Task SelectingBinaryNode_ReportsBinaryWithoutATextPreview()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashA,
            new[] { new WorkflowTreeNode("assets/logo.png", "logo.png", false, 2048, 2048, Now) },
            null,
            null,
            false);

        library.Preview.FilePreview = new WorkflowFilePreview(HashA, "assets/logo.png", 2048, true, false, null);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        viewModel.SelectedNode = viewModel.TreeNodes.Single();

        await viewModel.LoadFilePreviewAsync();

        Assert.True(viewModel.IsSelectedFileBinary);
        Assert.Empty(viewModel.SelectedFileContent);
        Assert.True(viewModel.HasSelectedFileNotice);
        Assert.Contains("бинарн", viewModel.SelectedFileNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectingDirectoryNode_ShowsNoFilePreview()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        library.Preview.TreePreview = new WorkflowTreePreview(
            HashA,
            new[] { new WorkflowTreeNode("src", "src", true, 0, 0, Now) },
            null,
            null,
            false);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        viewModel.SelectedNode = viewModel.TreeNodes.Single();

        await viewModel.LoadFilePreviewAsync();

        Assert.Empty(library.Preview.FileRequests);
        Assert.Empty(viewModel.SelectedFileContent);
        Assert.False(viewModel.IsSelectedFileBinary);
    }

    [Fact]
    public async Task ActiveVersion_IsMarkedOnlyForTheSelectedProjectBinding()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");
        library.AddProject("project-2");

        await library.BindingService.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-2");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        Assert.Equal("project-1", viewModel.SelectedProject!.Id);
        Assert.True(viewModel.Versions.Single(version => version.Id == "ver-2").IsActiveInCurrentProject);
        Assert.False(viewModel.Versions.Single(version => version.Id == "ver-1").IsActiveInCurrentProject);
        Assert.Equal("Active in this project", viewModel.Versions.Single(v => v.Id == "ver-2").ActiveStateDisplay);

        viewModel.SelectedProject = viewModel.Projects.Single(project => project.Id == "project-2");

        await viewModel.LoadVersionsAsync();

        // The other project has no binding, so nothing may be shown as active.
        Assert.All(viewModel.Versions, version => Assert.False(version.IsActiveInCurrentProject));
    }

    [Fact]
    public async Task BindToProject_CreatesTheBindingAndMarksTheVersionActive()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        Assert.True(viewModel.CanBindToProject);

        await viewModel.BindToProjectAsync();

        Assert.False(viewModel.HasBlocker);
        Assert.True(viewModel.HasCurrentBinding);
        Assert.Single(viewModel.Bindings);
        Assert.Equal("pkg-1", viewModel.Bindings[0].WorkflowPackageId);
        Assert.Equal("ver-1", viewModel.Bindings[0].ActiveVersionIdDisplay);
        Assert.True(viewModel.Versions.Single().IsActiveInCurrentProject);
        Assert.Contains("привязан", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindToProject_WithARoutePolicy_StoresItOnTheBinding()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.RoutePolicyId = "route-policy-1";

        await viewModel.BindToProjectAsync();

        Assert.Equal("route-policy-1", viewModel.Bindings.Single().Binding.RoutePolicyId);
        Assert.Equal("route-policy-1", viewModel.Bindings.Single().RoutePolicyDisplay);
    }

    [Fact]
    public async Task RepeatedBinding_PreservesTheOriginalBindingIdentity()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.BindToProjectAsync();

        var originalBindingId = viewModel.Bindings.Single().BindingId;

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-2");

        await viewModel.BindToProjectAsync();

        Assert.Single(viewModel.Bindings);
        Assert.Equal(originalBindingId, viewModel.Bindings.Single().BindingId);
        Assert.Equal("ver-2", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.Contains("обновлена", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActiveVersion_MovesTheActivePointer()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.BindToProjectAsync();

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-2");

        Assert.True(viewModel.CanSetActiveVersion);

        await viewModel.SetActiveVersionAsync();

        Assert.True(viewModel.Versions.Single(version => version.Id == "ver-2").IsActiveInCurrentProject);
        Assert.False(viewModel.Versions.Single(version => version.Id == "ver-1").IsActiveInCurrentProject);
        Assert.Equal("ver-2", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.Contains("Активирована", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActiveVersion_WithoutABinding_IsNotOffered()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanSetActiveVersion);

        await viewModel.SetActiveVersionAsync();

        Assert.Empty(viewModel.Bindings);
    }

    [Fact]
    public async Task Unbind_RemovesTheBindingButKeepsTheImmutablePackageAndVersion()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.BindToProjectAsync();

        Assert.True(viewModel.CanUnbind);

        await viewModel.UnbindAsync();

        Assert.False(viewModel.HasBindings);
        Assert.False(viewModel.HasCurrentBinding);
        Assert.All(viewModel.Versions, version => Assert.False(version.IsActiveInCurrentProject));

        // Unbinding never removes the package or the version.
        Assert.Single(viewModel.Packages);
        Assert.Single(viewModel.Versions);
        Assert.Contains("удалена", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportZip_ImportsAndSelectsTheImportedPackage()
    {
        var library = CreateLibrary();
        var package = CreatePackage("pkg-imported", HashC);
        var version = CreateVersion("pkg-imported", "ver-imported", 1, HashC);

        library.Import = new StubWorkflowImportService(library.Packages, library.Versions)
        {
            Result = new WorkflowImportResult(package, version, HashC, IsDuplicate: false)
        };

        var viewModel = library.CreateViewModel();

        var result = await viewModel.ImportZipAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "Imported");

        Assert.NotNull(result);
        Assert.Equal("Imported", library.Import!.LastPackageName);
        Assert.Equal(new byte[] { 1, 2, 3 }, library.Import.LastContent);
        Assert.Equal("pkg-imported", viewModel.SelectedPackage!.Id);
        Assert.Contains(viewModel.Packages, item => item.Id == "pkg-imported");
        Assert.Contains("Импортирован", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromZipPath_ReadsTheArchiveFromDisk()
    {
        var library = CreateLibrary();
        var package = CreatePackage("pkg-file", HashC);
        var version = CreateVersion("pkg-file", "ver-file", 1, HashC);

        library.Import = new StubWorkflowImportService(library.Packages, library.Versions)
        {
            Result = new WorkflowImportResult(package, version, HashC, IsDuplicate: false)
        };

        var viewModel = library.CreateViewModel();
        var zipPath = Path.Combine(Path.GetTempPath(), $"llmworkgui-{Guid.NewGuid():N}.zip");

        try
        {
            await File.WriteAllBytesAsync(zipPath, new byte[] { 9, 8, 7 });

            var result = await viewModel.ImportFromZipPathAsync(zipPath, "From file");

            Assert.NotNull(result);
            Assert.Equal("From file", library.Import.LastPackageName);
            Assert.Equal(new byte[] { 9, 8, 7 }, library.Import.LastContent);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Import_WithoutTheImportService_SurfacesABlocker()
    {
        var library = CreateLibrary();
        var viewModel = library.CreateViewModel();

        var result = await viewModel.ImportZipAsync(new MemoryStream(new byte[] { 1 }), "Imported");

        Assert.Null(result);
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("не настроена", viewModel.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFailure_SurfacesABlockerAndKeepsTheLibraryIntact()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);

        library.Import = new StubWorkflowImportService(library.Packages, library.Versions)
        {
            Failure = new WorkflowValidationException(WorkflowValidationFailure.InvalidArchive, "private-bare-canary")
        };

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        var result = await viewModel.ImportZipAsync(new MemoryStream(new byte[] { 1 }), "Broken");

        Assert.Null(result);
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("ZIP", viewModel.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain("private-bare-canary", viewModel.Blocker);
        Assert.Single(viewModel.Packages);
    }

    [Fact]
    public async Task ExportVersion_DelegatesToTheExportService()
    {
        var library = CreateLibrary();
        var viewModel = library.CreateViewModel();

        await viewModel.ExportVersionAsync("ver-1", @"C:\exports\ver-1.zip");

        Assert.Equal(new[] { ("ver-1", @"C:\exports\ver-1.zip") }, library.Export.Exports);
        Assert.Contains("экспортирована", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WithoutTheExportService_SurfacesABlocker()
    {
        var library = CreateLibrary(withExport: false);
        var viewModel = library.CreateViewModel();

        await viewModel.ExportVersionAsync("ver-1", @"C:\exports\ver-1.zip");

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("не настроена", viewModel.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewFailure_SurfacesABlockerInsteadOfAFabricatedPreview()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        library.Preview.TreeFailure = new WorkflowValidationException("Workflow blob is not a valid ZIP archive.");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Empty(viewModel.DocumentationContent);
        Assert.False(viewModel.HasDocumentation);
    }

    [Fact]
    public async Task BindingAVersionFromAnotherPackage_SurfacesABlocker()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddPackage("pkg-2", HashB);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-2", "ver-2", 1, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        // The foreign version is not part of the selected package, so the service must refuse the bind.
        viewModel.SelectedVersion = new WorkflowVersionItemViewModel(
            library.Versions.Get("ver-2")!);

        await viewModel.BindToProjectAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("does not belong", viewModel.Blocker, StringComparison.Ordinal);
        Assert.Empty(viewModel.Bindings);
    }

    [Fact]
    public async Task Refresh_KeepsTheSelectedPackage()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddPackage("pkg-2", HashB);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        viewModel.SelectedPackage = viewModel.Packages.Single(package => package.Id == "pkg-2");

        await viewModel.RefreshAsync();

        Assert.Equal("pkg-2", viewModel.SelectedPackage!.Id);
    }

    [Fact]
    public async Task WithoutThePreviewService_ThePreviewSectionIsNotOffered()
    {
        var library = CreateLibrary(withPreview: false);
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.LoadVersionsAsync();
        await viewModel.LoadPreviewAsync();

        Assert.False(viewModel.IsPreviewAvailable);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PreviewUnavailableNotice));
        Assert.Empty(viewModel.TreeNodes);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public void VersionItem_ReportsActiveStateTransitions()
    {
        var item = new WorkflowVersionItemViewModel(CreateVersion("pkg-1", "ver-1", 1, HashA));

        Assert.False(item.IsActiveInCurrentProject);
        Assert.Equal("Not active in this project", item.ActiveStateDisplay);

        item.IsActiveInCurrentProject = true;

        Assert.Equal("Active in this project", item.ActiveStateDisplay);
    }

    [Fact]
    public async Task Rollback_MovesTheActivePointerThroughTheActivationEngine()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-2");

        await viewModel.BindToProjectAsync();

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-1");

        Assert.True(viewModel.CanRollback);

        await viewModel.RollbackAsync();

        Assert.Equal(("project-1", "pkg-1", "ver-1"), Assert.Single(library.Activation.RollbackRequests));
        Assert.Equal("ver-1", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.True(viewModel.Versions.Single(version => version.Id == "ver-1").IsActiveInCurrentProject);
        Assert.Contains("байт-в-байт", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_WithoutTheActivationEngine_IsNotOffered()
    {
        var library = CreateLibrary(withActivation: false);
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();

        Assert.False(viewModel.IsActivationAvailable);
        Assert.False(viewModel.CanRollback);
        Assert.False(viewModel.CanBindToProject);
    }

    [Fact]
    public async Task BindToProject_WithSecretAndMissingModelBlockers_RefusesUntilEveryBlockerIsAcknowledged()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");
        library.Activation.Validation = CreateBlockedValidation();

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        await viewModel.BindToProjectAsync();

        // Nothing may move and nothing may be acknowledged implicitly.
        Assert.True(viewModel.HasBlocker);
        Assert.Empty(viewModel.Bindings);
        Assert.False(viewModel.HasCurrentBinding);
        Assert.False(viewModel.Versions.Single().IsActiveInCurrentProject);

        var request = Assert.Single(library.Activation.ActivationRequests);
        Assert.False(request.AcknowledgeBlockers);
        Assert.Empty(request.AcknowledgedBlockerIssues);

        Assert.True(viewModel.HasActivationBlockers);
        Assert.Equal(2, viewModel.ActivationBlockers.Count);
        Assert.False(viewModel.CanConfirmActivation);

        // One decision is not enough: every blocker row must be decided individually.
        viewModel.ActivationBlockers[0].IsAcknowledged = true;
        Assert.False(viewModel.CanConfirmActivation);

        viewModel.ActivationBlockers[1].IsAcknowledged = true;
        Assert.True(viewModel.CanConfirmActivation);

        await viewModel.ConfirmActivationAsync();

        var confirmation = library.Activation.ActivationRequests[^1];
        Assert.False(confirmation.AcknowledgeBlockers);
        Assert.Empty(confirmation.AcknowledgedBlockerKinds);
        Assert.Equal(
            new[] { AdaptationBlockerKind.MissingModel, AdaptationBlockerKind.DetectedSecret },
            confirmation.AcknowledgedBlockerIssues
                .Select(issue => issue.Kind)
                .OrderBy(kind => kind)
                .ToArray());
        Assert.Equal(
            CreateBlockedValidation().Issues.Select(issue => issue.Identity).OrderBy(identity => identity),
            confirmation.AcknowledgedBlockerIssues
                .Select(issue => issue.Identity)
                .OrderBy(identity => identity));
        Assert.Single(viewModel.Bindings);
        Assert.Equal("ver-1", viewModel.Bindings[0].ActiveVersionIdDisplay);
        Assert.False(viewModel.HasActivationBlockers);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public async Task SetActiveVersion_WithABlockedVersion_LeavesThePointerUntilEveryDecisionIsRecorded()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.BindToProjectAsync();

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-2");
        library.Activation.Validation = CreateBlockedValidation();

        await viewModel.SetActiveVersionAsync();

        Assert.Equal("ver-1", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.True(viewModel.HasActivationBlockers);
        Assert.False(viewModel.CanConfirmActivation);

        viewModel.ActivationBlockers[0].IsAcknowledged = true;
        viewModel.ActivationBlockers[1].IsAcknowledged = true;

        await viewModel.ConfirmActivationAsync();

        var confirmation = library.Activation.ActivationRequests[^1];
        Assert.Equal(2, confirmation.AcknowledgedBlockerIssues.Count);
        Assert.Empty(confirmation.AcknowledgedBlockerKinds);
        Assert.Equal("ver-2", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.True(viewModel.Versions.Single(version => version.Id == "ver-2").IsActiveInCurrentProject);
    }

    [Fact]
    public async Task Rollback_WithABlockedTarget_LeavesThePointerUntilEveryDecisionIsRecorded()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddVersion("pkg-1", "ver-2", 2, HashB);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();
        await viewModel.BindToProjectAsync();

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.Id == "ver-2");
        library.Activation.Validation = CreateBlockedValidation();

        await viewModel.RollbackAsync();

        Assert.Equal("ver-1", viewModel.Bindings.Single().ActiveVersionIdDisplay);
        Assert.True(viewModel.HasActivationBlockers);
        Assert.Equal(2, viewModel.ActivationBlockers.Count);
        Assert.DoesNotContain(viewModel.ActivationBlockers, blocker => blocker.IsAcknowledged);

        viewModel.ActivationBlockers[0].IsAcknowledged = true;
        viewModel.ActivationBlockers[1].IsAcknowledged = true;

        await viewModel.ConfirmActivationAsync();

        var confirmation = library.Activation.RollbackAcknowledgements[^1];
        Assert.Equal(2, confirmation.AcknowledgedBlockerIssues.Count);
        Assert.Empty(confirmation.AcknowledgedBlockerKinds);
        Assert.Equal("ver-2", viewModel.Bindings.Single().ActiveVersionIdDisplay);
    }

    [Fact]
    public async Task DismissActivationBlockers_KeepsThePointerAndClearsTheDecisionPanel()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");
        library.Activation.Validation = CreateBlockedValidation();

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        await viewModel.BindToProjectAsync();

        viewModel.DismissActivationBlockersCommand.Execute(null);

        Assert.False(viewModel.HasActivationBlockers);
        Assert.False(viewModel.HasBlocker);
        Assert.Empty(viewModel.Bindings);
        Assert.False(viewModel.CanConfirmActivation);
    }

    [Fact]
    public async Task AdaptWorkflow_OpensTheDialogForTheSelectedVersionAndProject()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsAdaptationAvailable);
        Assert.True(viewModel.CanAdaptWorkflow);

        await viewModel.AdaptWorkflowAsync();

        Assert.True(viewModel.AdaptationDialog.IsVisible);
        Assert.False(viewModel.AdaptationDialog.IsSessionActive);
        Assert.Equal("ver-1", viewModel.AdaptationDialog.SourceVersion!.Id);
        Assert.Equal("pkg-1", viewModel.AdaptationDialog.Package!.Id);
        Assert.Equal("project-1", viewModel.AdaptationDialog.Project!.Id);
        // The route id is a composite identity, not the bare account id.
        var route = Assert.Single(viewModel.AdaptationDialog.AvailableRoutes);

        Assert.NotEqual("model-1", route.Id);
        Assert.True(AdaptationRouteIdentity.TryParse(route.Id, out var identity));
        Assert.Equal("model-1", identity.AccountId);
        Assert.Equal("opencode/primary-model", identity.BackendModelId);
    }

    [Fact]
    public async Task BindToProject_WhenRevalidationReportsAnotherIssue_KeepsThePointerAndReopensTheDecision()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");
        library.Activation.Validation = CreateBlockedValidation();

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        await viewModel.BindToProjectAsync();

        Assert.True(viewModel.HasActivationBlockers);
        Assert.Equal(2, viewModel.ActivationBlockers.Count);

        foreach (var blocker in viewModel.ActivationBlockers)
        {
            blocker.IsAcknowledged = true;
        }

        Assert.True(viewModel.CanConfirmActivation);

        var reported = CreateBlockedValidation().Issues;

        // The revalidation no longer reports the same issues: the same kind for another model appeared.
        library.Activation.Validation = new WorkflowActivationValidationResult(new[]
        {
            reported[0],
            reported[1],
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingModel,
                "Executor",
                "Model 'model-another' not found in the current catalog.")
        });

        await viewModel.ConfirmActivationAsync();

        // The stale checkmarks are not reused: every freshly reported issue has to be decided again.
        Assert.Equal(3, viewModel.ActivationBlockers.Count);
        Assert.DoesNotContain(viewModel.ActivationBlockers, blocker => blocker.IsAcknowledged);
        Assert.False(viewModel.CanConfirmActivation);
        Assert.Empty(viewModel.Bindings);

        var refused = library.Activation.ActivationRequests[^1];

        Assert.Equal(2, refused.AcknowledgedBlockerIssues.Count);
        Assert.Equal(2, library.Activation.ActivationRequests.Count);
    }

    [Fact]
    public async Task BindToProject_WhenRevalidationReportsTheSameIssues_KeepsTheRecordedDecisions()
    {
        var library = CreateLibrary();
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");
        library.Activation.Validation = CreateBlockedValidation();

        var viewModel = library.CreateViewModel();
        await viewModel.RefreshAsync();

        await viewModel.BindToProjectAsync();

        foreach (var blocker in viewModel.ActivationBlockers)
        {
            blocker.IsAcknowledged = true;
        }

        await viewModel.ConfirmActivationAsync();

        Assert.False(viewModel.HasActivationBlockers);
        Assert.Single(viewModel.Bindings);
        Assert.Equal("ver-1", viewModel.Bindings[0].ActiveVersionIdDisplay);
    }

    [Fact]
    public void AdaptWorkflow_WithoutTheAdaptationService_IsNotOffered()
    {
        var library = CreateLibrary(withAdaptation: false);
        library.AddPackage("pkg-1", HashA);
        library.AddVersion("pkg-1", "ver-1", 1, HashA);
        library.AddProject("project-1");

        var viewModel = library.CreateViewModel();

        Assert.False(viewModel.IsAdaptationAvailable);
        Assert.False(viewModel.CanAdaptWorkflow);
    }

    private static TestWorkflowLibrary CreateLibrary(
        bool withPreview = true,
        bool withExport = true,
        bool withProjects = true,
        bool withActivation = true,
        bool withAdaptation = true) =>
        new(withPreview, withExport, withProjects, withActivation, withAdaptation);

    private static WorkflowActivationValidationResult CreateBlockedValidation() =>
        new(new[]
        {
            new AdaptationValidationIssue(
                AdaptationBlockerKind.DetectedSecret,
                role: null,
                "Secret 'ApiKey' detected at config/secrets.env:3."),
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingModel,
                "Executor",
                "Model 'model-missing' not found in the current catalog.")
        });

    private static WorkflowPackage CreatePackage(string packageId, string hash) =>
        new(
            packageId,
            $"Package {packageId}",
            "Imported workflow",
            new[] { "workflow" },
            WorkflowSourceType.ZipArchive,
            hash,
            hash,
            Now,
            Now);

    private static WorkflowVersion CreateVersion(
        string packageId,
        string versionId,
        int versionNumber,
        string hash) =>
        new(
            versionId,
            packageId,
            versionNumber,
            hash,
            hash,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Now,
            null);

    private sealed class TestWorkflowLibrary
    {
        private readonly bool _withPreview;
        private readonly bool _withExport;
        private readonly bool _withProjects;
        private readonly bool _withActivation;
        private readonly bool _withAdaptation;

        public TestWorkflowLibrary(
            bool withPreview,
            bool withExport,
            bool withProjects,
            bool withActivation,
            bool withAdaptation)
        {
            _withPreview = withPreview;
            _withExport = withExport;
            _withProjects = withProjects;
            _withActivation = withActivation;
            _withAdaptation = withAdaptation;

            BindingService = new WorkflowBindingService(
                Bindings,
                Packages,
                Versions,
                new FixedTimeProvider(Now));

            Activation = new LibraryStubWorkflowActivationService(Bindings, Versions);
        }

        public InMemoryWorkflowPackageRepository Packages { get; } = new();

        public InMemoryWorkflowVersionRepository Versions { get; } = new();

        public InMemoryWorkflowBindingRepository Bindings { get; } = new();

        public StubWorkflowPreviewService Preview { get; } = new();

        public StubWorkflowExportService Export { get; } = new();

        public InMemoryProjectRepository Projects { get; } = new();

        public LibraryStubWorkflowActivationService Activation { get; }

        public LibraryStubWorkflowAdaptationService Adaptation { get; } = new();

        public LibraryStubCatalogProvider Catalog { get; } = new();

        public StubWorkflowImportService? Import { get; set; }

        public WorkflowBindingService BindingService { get; }

        public void AddPackage(string packageId, string hash) =>
            Packages.UpsertAsync(CreatePackage(packageId, hash)).GetAwaiter().GetResult();

        public void AddVersion(string packageId, string versionId, int versionNumber, string hash) =>
            Versions.UpsertAsync(CreateVersion(packageId, versionId, versionNumber, hash))
                .GetAwaiter()
                .GetResult();

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

        public WorkflowLibraryViewModel CreateViewModel() =>
            new(
                Packages,
                Versions,
                Bindings,
                BindingService,
                _withPreview ? Preview : null,
                Import,
                _withExport ? Export : null,
                _withProjects ? Projects : null,
                _withActivation ? Activation : null,
                _withAdaptation ? Adaptation : null,
                _withAdaptation ? Catalog : null);
    }
}

internal sealed class LibraryStubCatalogProvider : ISanitizedCatalogProvider
{
    public SanitizedCapabilityCatalog Catalog { get; set; } = new(
        Array.Empty<SanitizedProviderInfo>(),
        new[]
        {
            new SanitizedModelInfo(
                "model-1",
                "Primary Model",
                ModelCapabilityFlags.Chat,
                Array.Empty<string>(),
                new[] { "balanced" },
                ContextWindow: 128000,
                HealthState.Healthy,
                IsRoutable: true)
            {
                AccountId = "model-1",
                ProviderProfileId = "prov-1",
                Backend = BackendType.OpenCode,
                BackendModelId = "opencode/primary-model"
            }
        },
        DateTimeOffset.UtcNow);

    public Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Catalog);
}

internal sealed class LibraryStubWorkflowAdaptationService : IWorkflowAdaptationService
{
    public AdaptationPreSendPreview? Preview { get; set; }

    public Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
        string workflowVersionId,
        string adapterRouteId,
        AdaptationGoal goal,
        IReadOnlyList<string>? userExcludedFiles = null,
        bool allowExpandedSemanticScope = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Preview ?? new AdaptationPreSendPreview(
                workflowVersionId,
                sourceVersionNumber: 1,
                "sha256:" + new string('a', 64),
                adapterRouteId,
                adapterRouteId,
                goal,
                WorkflowSecretScanReport.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new SanitizedCapabilityCatalog(
                    Array.Empty<SanitizedProviderInfo>(),
                    Array.Empty<SanitizedModelInfo>(),
                    DateTimeOffset.UtcNow),
                AdaptationPreSendPreview.UnknownQuotaValue,
                AdaptationPreSendPreview.UnknownQuotaValue,
                reserveThreshold: null,
                promptPreview: string.Empty));

    public Task<AdaptationCandidateResult> StartAdaptationAsync(
        AdaptationExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library tests do not start adaptation turns.");

    public Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
        AdaptationFollowUpRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library tests do not submit follow-up turns.");

    public Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library tests do not save candidates.");

    public Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library tests do not discard sessions.");

    public AdaptationSessionSnapshot GetSessionSnapshot(string sessionId) =>
        throw new NotSupportedException("The workflow library tests do not read session snapshots.");
}

internal sealed class LibraryStubWorkflowActivationService : IWorkflowActivationService
{
    private readonly IWorkflowBindingRepository? _bindingRepository;
    private readonly IWorkflowVersionRepository? _versionRepository;

    public LibraryStubWorkflowActivationService(
        IWorkflowBindingRepository? bindingRepository = null,
        IWorkflowVersionRepository? versionRepository = null)
    {
        _bindingRepository = bindingRepository;
        _versionRepository = versionRepository;
    }

    public List<(string ProjectId, string WorkflowPackageId, string TargetVersionId)> RollbackRequests { get; } = new();

    public List<WorkflowActivationRequest> ActivationRequests { get; } = new();

    public List<WorkflowActivationRequest> RollbackAcknowledgements { get; } = new();

    public WorkflowActivationValidationResult Validation { get; set; } =
        WorkflowActivationValidationResult.Valid;

    public Task<WorkflowActivationValidationResult> ValidateForActivationAsync(
        string workflowVersionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Validation);

    public async Task<WorkflowActivationResult> ActivateVersionAsync(
        WorkflowActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ActivationRequests.Add(request);

        if (!AreBlockersAcknowledged(request))
        {
            return WorkflowActivationResult.Blocked(
                Validation,
                "Activation blocked: candidate has unresolved blockers that were not acknowledged.");
        }

        if (await ResolveVersionMismatchAsync(request.WorkflowPackageId, request.WorkflowVersionId)
            is { } mismatch)
        {
            return WorkflowActivationResult.Failed(mismatch, Validation);
        }

        var binding = await UpsertBindingAsync(
            request.ProjectId,
            request.WorkflowPackageId,
            request.WorkflowVersionId,
            request.RoutePolicyId,
            preserveExistingRoutePolicy: request.PreserveExistingRoutePolicy,
            cancellationToken);

        return WorkflowActivationResult.Success(binding, Validation);
    }

    public async Task<WorkflowRollbackResult> RollbackToVersionAsync(
        string projectId,
        string workflowPackageId,
        string targetVersionId,
        bool acknowledgeBlockers = false,
        IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
        IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
        CancellationToken cancellationToken = default)
    {
        RollbackRequests.Add((projectId, workflowPackageId, targetVersionId));
        RollbackAcknowledgements.Add(new WorkflowActivationRequest(
            projectId,
            workflowPackageId,
            targetVersionId,
            acknowledgeBlockers,
            routePolicyId: null,
            acknowledgedBlockerKinds,
            acknowledgedBlockerIssues));

        if (!Validation.IsFullyAcknowledgedBy(acknowledgedBlockerIssues))
        {
            return WorkflowRollbackResult.Blocked(
                Validation,
                targetVersionId,
                "Rollback blocked: target version has unresolved blockers that were not acknowledged.");
        }

        if (await ResolveVersionMismatchAsync(workflowPackageId, targetVersionId) is { } mismatch)
        {
            return WorkflowRollbackResult.Failed(targetVersionId, mismatch);
        }

        var binding = await UpsertBindingAsync(
            projectId,
            workflowPackageId,
            targetVersionId,
            routePolicyId: null,
            preserveExistingRoutePolicy: true,
            cancellationToken);

        return WorkflowRollbackResult.Success(binding, null, targetVersionId);
    }

    private async Task<WorkflowBinding> UpsertBindingAsync(
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        string? routePolicyId,
        bool preserveExistingRoutePolicy,
        CancellationToken cancellationToken)
    {
        var existing = _bindingRepository is null
            ? null
            : await _bindingRepository.GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var effectiveRoutePolicyId = existing is not null && preserveExistingRoutePolicy
            ? existing.RoutePolicyId
            : routePolicyId;

        var binding = existing is null
            ? new WorkflowBinding(
                Guid.NewGuid().ToString("N"),
                projectId,
                workflowPackageId,
                activeVersionId,
                effectiveRoutePolicyId,
                now,
                now)
            : new WorkflowBinding(
                existing.Id,
                existing.ProjectId,
                existing.WorkflowPackageId,
                activeVersionId,
                effectiveRoutePolicyId,
                existing.CreatedAtUtc,
                now);

        if (_bindingRepository is not null)
        {
            await _bindingRepository.UpsertAsync(binding, cancellationToken);
        }

        return binding;
    }

    private bool AreBlockersAcknowledged(WorkflowActivationRequest request) =>
        Validation.IsFullyAcknowledgedBy(request.AcknowledgedBlockerIssues);

    private async Task<string?> ResolveVersionMismatchAsync(string workflowPackageId, string versionId)
    {
        if (_versionRepository is null)
        {
            return null;
        }

        var version = await _versionRepository.GetByIdAsync(versionId);

        if (version is null)
        {
            return $"The workflow version '{versionId}' does not exist.";
        }

        return string.Equals(version.WorkflowPackageId, workflowPackageId, StringComparison.Ordinal)
            ? null
            : $"The workflow version '{versionId}' does not belong to package '{workflowPackageId}'.";
    }
}

internal sealed class InMemoryWorkflowPackageRepository : IWorkflowPackageRepository
{
    private readonly Dictionary<string, WorkflowPackage> _packages = new(StringComparer.Ordinal);

    public Task UpsertAsync(WorkflowPackage package, CancellationToken cancellationToken = default)
    {
        _packages[package.Id] = package;
        return Task.CompletedTask;
    }

    public Task<WorkflowPackage?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_packages.GetValueOrDefault(id));

    public Task<WorkflowPackage?> GetByOriginalHashAsync(
        string originalHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_packages.Values.FirstOrDefault(
            package => string.Equals(package.OriginalHash, originalHash, StringComparison.Ordinal)));

    public Task<IReadOnlyList<WorkflowPackage>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowPackage>>(
            _packages.Values.OrderBy(package => package.Id, StringComparer.Ordinal).ToList());

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_packages.Remove(id));
}

internal sealed class InMemoryWorkflowVersionRepository : IWorkflowVersionRepository
{
    private readonly Dictionary<string, WorkflowVersion> _versions = new(StringComparer.Ordinal);
    public Exception? ListFailure { get; set; }

    public WorkflowVersion? Get(string id) => _versions.GetValueOrDefault(id);

    public Task UpsertAsync(WorkflowVersion version, CancellationToken cancellationToken = default)
    {
        _versions[version.Id] = version;
        return Task.CompletedTask;
    }

    public Task<WorkflowVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_versions.GetValueOrDefault(id));

    public Task<WorkflowVersion?> GetByPackageAndVersionAsync(
        string packageId,
        int versionNumber,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_versions.Values.FirstOrDefault(
            version => version.WorkflowPackageId == packageId && version.VersionNumber == versionNumber));

    public Task<IReadOnlyList<WorkflowVersion>> ListByPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        ListFailure is not null ? Task.FromException<IReadOnlyList<WorkflowVersion>>(ListFailure)
        : Task.FromResult<IReadOnlyList<WorkflowVersion>>(
            _versions.Values
                .Where(version => version.WorkflowPackageId == packageId)
                .OrderBy(version => version.VersionNumber)
                .ThenBy(version => version.Id, StringComparer.Ordinal)
                .ToList());

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_versions.Remove(id));
}

/// <summary>
/// Mirrors the SQLite unique (ProjectId, WorkflowPackageId) rule: a repeated pair replaces the stored
/// row instead of adding a second binding.
/// </summary>
internal sealed class InMemoryWorkflowBindingRepository : IWorkflowBindingRepository
{
    private readonly Dictionary<string, WorkflowBinding> _bindings = new(StringComparer.Ordinal);

    public Func<string, Task<IReadOnlyList<WorkflowBinding>>>? ListHandler { get; set; }
    public Func<string, string, Task<WorkflowBinding?>>? GetHandler { get; set; }

    public Task UpsertAsync(WorkflowBinding binding, CancellationToken cancellationToken = default)
    {
        lock (_bindings)
        {
        var existing = _bindings.Values.FirstOrDefault(
            candidate => candidate.ProjectId == binding.ProjectId
                && candidate.WorkflowPackageId == binding.WorkflowPackageId);

        if (existing is not null)
        {
            _bindings.Remove(existing.Id);
        }

        _bindings[binding.Id] = binding;
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowBinding?> TrySetActiveVersionAsync(WorkflowBinding expected, string activeVersionId,
        DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindings)
        {
            var current = _bindings.GetValueOrDefault(expected.Id);
            if (current is null || current.ProjectId != expected.ProjectId || current.WorkflowPackageId != expected.WorkflowPackageId
                || current.ActiveVersionId != expected.ActiveVersionId) return Task.FromResult<WorkflowBinding?>(null);
            var updated = new WorkflowBinding(current.Id, current.ProjectId, current.WorkflowPackageId, activeVersionId,
                current.RoutePolicyId, current.CreatedAtUtc, updatedAtUtc);
            _bindings[current.Id] = updated;
            return Task.FromResult<WorkflowBinding?>(updated);
        }
    }

    public Task<WorkflowBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bindings.GetValueOrDefault(id));

    public Task<WorkflowBinding?> GetByProjectAndPackageAsync(
        string projectId,
        string workflowPackageId,
        CancellationToken cancellationToken = default) =>
        GetHandler is not null ? GetHandler(projectId, workflowPackageId) : Task.FromResult(_bindings.Values.FirstOrDefault(
            binding => binding.ProjectId == projectId
                && binding.WorkflowPackageId == workflowPackageId));

    public Task<IReadOnlyList<WorkflowBinding>> ListByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        ListHandler is not null ? ListHandler(projectId) : Task.FromResult<IReadOnlyList<WorkflowBinding>>(
            _bindings.Values
                .Where(binding => binding.ProjectId == projectId)
                .OrderBy(binding => binding.CreatedAtUtc)
                .ThenBy(binding => binding.Id, StringComparer.Ordinal)
                .ToList());

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bindings.Remove(id));
}

internal sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);

    public Task UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }

    public Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.GetValueOrDefault(projectId));

    public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.Values.FirstOrDefault(
            project => string.Equals(project.RootPath, rootPath, StringComparison.Ordinal)));

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Project>>(
            _projects.Values.OrderBy(project => project.Id, StringComparer.Ordinal).ToList());

    public Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.Remove(projectId));
}

internal sealed class StubWorkflowPreviewService : IWorkflowPreviewService
{
    public List<string> TreeRequests { get; } = new();

    public List<(string BlobId, string Path)> FileRequests { get; } = new();

    public WorkflowTreePreview? TreePreview { get; set; }

    public WorkflowFilePreview? FilePreview { get; set; }

    public Exception? TreeFailure { get; set; }

    public Task<WorkflowTreePreview> GetTreePreviewAsync(
        string blobId,
        CancellationToken cancellationToken = default)
    {
        TreeRequests.Add(blobId);

        if (TreeFailure is not null)
        {
            throw TreeFailure;
        }

        return Task.FromResult(
            TreePreview ?? new WorkflowTreePreview(blobId, Array.Empty<WorkflowTreeNode>(), null, null, false));
    }

    public Task<WorkflowFilePreview> GetFilePreviewAsync(
        string blobId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        FileRequests.Add((blobId, relativePath));

        return Task.FromResult(
            FilePreview ?? new WorkflowFilePreview(blobId, relativePath, 0, false, false, string.Empty));
    }
}

internal sealed class StubWorkflowImportService : IWorkflowImportService
{
    private readonly IWorkflowPackageRepository _packageRepository;
    private readonly IWorkflowVersionRepository _versionRepository;

    public StubWorkflowImportService(
        IWorkflowPackageRepository packageRepository,
        IWorkflowVersionRepository versionRepository)
    {
        _packageRepository = packageRepository;
        _versionRepository = versionRepository;
    }

    public WorkflowImportResult? Result { get; set; }

    public Exception? Failure { get; set; }

    public string? LastPackageName { get; private set; }

    public byte[]? LastContent { get; private set; }

    public async Task<WorkflowImportResult> ImportZipAsync(
        Stream archiveStream,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        LastPackageName = packageName;

        using var buffer = new MemoryStream();
        await archiveStream.CopyToAsync(buffer, cancellationToken);
        LastContent = buffer.ToArray();

        if (Failure is not null)
        {
            throw Failure;
        }

        var result = Result
            ?? throw new InvalidOperationException("The import stub has no configured result.");

        await _packageRepository.UpsertAsync(result.Package, cancellationToken);
        await _versionRepository.UpsertAsync(result.Version, cancellationToken);

        return result;
    }

    public Task<WorkflowImportResult> ImportDirectoryAsync(
        string directoryPath,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library does not import directories in this slice.");
}

internal sealed class StubWorkflowExportService : IWorkflowExportService
{
    public List<(string VersionId, string TargetPath)> Exports { get; } = new();

    public Task<Stream> OpenVersionExportStreamAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The workflow library exports to a file path in this slice.");

    public Task ExportToFileAsync(
        string versionId,
        string destinationFilePath,
        CancellationToken cancellationToken = default)
    {
        Exports.Add((versionId, destinationFilePath));

        return Task.CompletedTask;
    }

    // This fixture records the command intent only; real file collision evidence lives in the SQLite/DI fixture.
    public Task ExportToNewFileAsync(string versionId, string destinationFilePath, CancellationToken cancellationToken = default) =>
        ExportToFileAsync(versionId, destinationFilePath, cancellationToken);
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
