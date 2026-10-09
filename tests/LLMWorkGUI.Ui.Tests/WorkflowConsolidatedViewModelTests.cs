using System;
using System.IO;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behavior of the Phase 11C consolidated Workflow Console: the six compact studio/monitor
/// surfaces stay synchronized with the embedded panels, and the one-click workflow package import/export
/// reuses the immutable library contracts.
/// </summary>
public sealed class WorkflowConsolidatedViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Console_ExposesAllSixCompactModesAndSynchronizesTheEmbeddedPanels()
    {
        var library = CreateLibrary();
        var console = new WorkflowConsolidatedViewModel(library);

        Assert.True(console.IsAvailable);
        Assert.True(console.IsTemplatesMode);
        Assert.True(console.IsStudioVisible);
        Assert.Equal(WorkflowStudioPanelMode.Templates, library.Studio.ActivePanelMode);

        console.SelectMode(WorkflowConsoleMode.StudioRoles);
        Assert.True(console.IsRoleMatrixMode);
        Assert.Equal(WorkflowStudioPanelMode.RoleMatrix, library.Studio.ActivePanelMode);
        Assert.Contains("Матрица ролей", console.ActiveModeDisplay, StringComparison.Ordinal);

        console.SelectMode(WorkflowConsoleMode.StudioDocuments);
        Assert.Equal(WorkflowStudioPanelMode.Documents, library.Studio.ActivePanelMode);

        console.SelectMode(WorkflowConsoleMode.MonitorSchema);
        Assert.True(console.IsMonitorMode);
        Assert.True(console.IsMonitorVisible);
        Assert.Equal(WorkflowMonitorPanelMode.Schema, library.ActivityMonitor.ActivePanelMode);

        console.SelectMode(WorkflowConsoleMode.MonitorActivity);
        Assert.Equal(WorkflowMonitorPanelMode.Activity, library.ActivityMonitor.ActivePanelMode);
        Assert.Contains("Поток активности", console.ActiveModeDisplay, StringComparison.Ordinal);

        console.SelectMode(WorkflowConsoleMode.MonitorInspector);
        Assert.Equal(WorkflowMonitorPanelMode.Inspector, library.ActivityMonitor.ActivePanelMode);
        Assert.Contains("Инспектор", console.ActiveModeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleMatrixMode_ShowsTheRolesOfTheSelectedBuiltInTemplate()
    {
        var library = CreateLibrary();
        var console = new WorkflowConsolidatedViewModel(library);

        await library.Studio.RefreshTemplatesAsync();
        console.SelectMode(WorkflowConsoleMode.StudioRoles);

        Assert.True(library.Studio.HasRoleMatrix);
        Assert.NotEmpty(library.Studio.RoleMatrix);
        Assert.All(library.Studio.RoleMatrix, role => Assert.False(string.IsNullOrWhiteSpace(role.RoleDisplay)));
        Assert.Contains(
            library.Studio.RoleMatrix,
            role => role.SummaryDisplay.Contains("этапы:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuickImport_ImportsTheZipAtTheConfiguredPathInOneCall()
    {
        var packages = new InMemoryWorkflowPackageRepository();
        var versions = new InMemoryWorkflowVersionRepository();
        var importService = new StubWorkflowImportService(packages, versions);
        var package = CreatePackage("console-package", "Console Package");
        var version = CreateVersion(package.Id, 1, 'a');
        var blobId = "sha256:" + new string('b', 64);

        importService.Result = new WorkflowImportResult(package, version, blobId, IsDuplicate: false);

        var library = CreateLibrary(packages, versions, importService);
        var console = new WorkflowConsolidatedViewModel(library);
        var zipPath = Path.Combine(Path.GetTempPath(), "llmworkgui-console-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await File.WriteAllBytesAsync(zipPath, new byte[] { 1, 2, 3, 4 });
            library.QuickImportPath = zipPath;

            Assert.True(library.CanQuickImport);
            Assert.Empty(await packages.ListAsync());

            var result = await console.QuickImportAsync();

            Assert.NotNull(result);
            Assert.Single(await packages.ListAsync());
            Assert.Equal("Imported workflow", importService.LastPackageName);
            Assert.Contains("Быстрый импорт", library.StatusMessage, StringComparison.Ordinal);
            Assert.False(library.HasBlocker);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task QuickExport_WritesTheSelectedVersionThroughTheExportService()
    {
        var packages = new InMemoryWorkflowPackageRepository();
        var versions = new InMemoryWorkflowVersionRepository();
        var exportService = new StubWorkflowExportService();
        var package = CreatePackage("console-package", "ConsolePackage");
        var version = CreateVersion(package.Id, 1, 'a');

        await packages.UpsertAsync(package);
        await versions.UpsertAsync(version);

        var library = CreateLibrary(packages, versions, exportService: exportService);
        await library.RefreshAsync();

        var directory = CreateTempDirectory();

        try
        {
            library.QuickExportDirectory = directory;
            var console = new WorkflowConsolidatedViewModel(library);

            Assert.True(library.CanQuickExport);

            var exportedPath = await console.QuickExportAsync();

            Assert.NotNull(exportedPath);
            Assert.EndsWith("ConsolePackage-v1.zip", exportedPath, StringComparison.Ordinal);
            Assert.Single(exportService.Exports);
            Assert.Equal(version.Id, exportService.Exports[0].VersionId);
            Assert.Equal(exportedPath, library.LastQuickExportPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task QuickExport_RefusesToOverwriteAnExistingFile()
    {
        var packages = new InMemoryWorkflowPackageRepository();
        var versions = new InMemoryWorkflowVersionRepository();
        var exportService = new StubWorkflowExportService();
        var package = CreatePackage("console-package", "ConsolePackage");
        var version = CreateVersion(package.Id, 1, 'a');

        await packages.UpsertAsync(package);
        await versions.UpsertAsync(version);

        var library = CreateLibrary(packages, versions, exportService: exportService);
        await library.RefreshAsync();

        var directory = CreateTempDirectory();
        var existing = Path.Combine(directory, "ConsolePackage-v1.zip");

        try
        {
            await File.WriteAllTextAsync(existing, "already here");
            library.QuickExportDirectory = directory;

            var result = await library.QuickExportAsync();

            Assert.Null(result);
            Assert.Empty(exportService.Exports);
            Assert.Contains("не перезаписывает", library.Blocker, StringComparison.Ordinal);
            Assert.Equal("already here", await File.ReadAllTextAsync(existing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WithoutLibrary_TheConsoleReportsItselfUnavailable()
    {
        var console = new WorkflowConsolidatedViewModel();

        Assert.False(console.IsAvailable);
        Assert.Null(console.Studio);
        Assert.Null(console.Monitor);
        Assert.Equal("Консоль сценариев не сконфигурирована: библиотека сценариев недоступна в этой композиции.", console.AvailabilityDisplay);
        Assert.Equal(WorkflowConsolidatedViewModel.NotReportedPlaceholder, console.SelectedWorkflowDisplay);
        Assert.Equal(WorkflowConsolidatedViewModel.NotReportedPlaceholder, console.RunSummaryDisplay);
        Assert.Null(await console.QuickImportAsync());
        Assert.Null(await console.QuickExportAsync());

        // Mode navigation still works without a library so the switcher never throws.
        console.SelectMode(WorkflowConsoleMode.MonitorInspector);
        Assert.True(console.IsInspectorMode);
    }

    [Fact]
    public void Shell_UsesRussianConsoleHintAndQuickExportNotice()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var console = new WorkflowConsolidatedViewModel(CreateLibrary());
        var shell = new UnifiedWorkspaceShellViewModel(main, workflowConsole: console);

        Assert.Equal("Студия и монитор открываются в единой консоли сценариев.", shell.WorkflowConsoleHint);
        shell.ExportWorkflow();
        Assert.Equal("Выберите версию сценария в консоли сценариев для быстрого экспорта.", shell.ShellNotice);

        var unavailableShell = new UnifiedWorkspaceShellViewModel(main);
        Assert.Equal("Workflow Console не сконфигурирован в этой композиции.", unavailableShell.WorkflowConsoleHint);
    }

    private static WorkflowLibraryViewModel CreateLibrary(
        InMemoryWorkflowPackageRepository? packages = null,
        InMemoryWorkflowVersionRepository? versions = null,
        StubWorkflowImportService? importService = null,
        StubWorkflowExportService? exportService = null) =>
        new(
            packageRepository: packages,
            versionRepository: versions,
            importService: importService,
            exportService: exportService);

    private static WorkflowPackage CreatePackage(string id, string name) =>
        new(
            id,
            name,
            description: null,
            tags: Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            "sha256:" + new string('c', 64),
            "sha256:" + new string('c', 64),
            Now,
            Now);

    private static WorkflowVersion CreateVersion(string packageId, int number, char hashCharacter) =>
        new(
            $"version-{number}",
            packageId,
            number,
            "sha256:" + new string(hashCharacter, 64),
            "sha256:" + new string('c', 64),
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: null,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            Now,
            activatedAtUtc: null);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-console-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }
}
