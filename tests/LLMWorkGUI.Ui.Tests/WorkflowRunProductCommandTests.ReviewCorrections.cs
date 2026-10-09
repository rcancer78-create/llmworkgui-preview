using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowRunProductCommandTests
{
    [Fact]
    public async Task Review_QuickExportDoesNotOverwriteAFileCreatedAfterItsPreflightCheck()
    {
        using var host = await CreateInitializedHostAsync();
        var export = new CompetingFileExportService(host.Services.GetRequiredService<IWorkflowExportService>());
        var vm = new WorkflowLibraryViewModel(
            host.Services.GetRequiredService<IWorkflowPackageRepository>(),
            host.Services.GetRequiredService<IWorkflowVersionRepository>(),
            host.Services.GetRequiredService<IWorkflowBindingRepository>(),
            host.Services.GetRequiredService<IWorkflowBindingService>(),
            exportService: export,
            projectRepository: host.Services.GetRequiredService<IProjectRepository>());
        await vm.RefreshAsync();
        vm.QuickExportDirectory = Path.Combine(_root, "review-quick-export");

        var result = await vm.QuickExportAsync();

        Assert.NotNull(export.CompetingPath);
        Assert.Equal(CompetingFileExportService.CompetingBytes, await File.ReadAllBytesAsync(export.CompetingPath!));
        Assert.Null(result);
        Assert.True(vm.HasBlocker);
        Assert.False(vm.HasQuickExportPath);
    }

    private sealed class CompetingFileExportService(IWorkflowExportService inner) : IWorkflowExportService
    {
        internal static readonly byte[] CompetingBytes = "independently-created-file"u8.ToArray();
        internal string? CompetingPath { get; private set; }
        public Task<Stream> OpenVersionExportStreamAsync(string id, CancellationToken token = default) =>
            inner.OpenVersionExportStreamAsync(id, token);
        public async Task ExportToFileAsync(string id, string destination, CancellationToken token = default)
        {
            CompetingPath = destination;
            await File.WriteAllBytesAsync(destination, CompetingBytes, token);
            await inner.ExportToFileAsync(id, destination, token);
        }
        public async Task ExportToNewFileAsync(string id, string destination, CancellationToken token = default)
        {
            CompetingPath = destination;
            await File.WriteAllBytesAsync(destination, CompetingBytes, token);
            await inner.ExportToNewFileAsync(id, destination, token);
        }
    }

    [Fact]
    public async Task Review_ChangingVersionImmediatelyRetiresThePreviousRunWhileSqliteObservationIsPending()
    {
        var gate = new RunObservationGate();
        using var host = await CreateInitializedHostAsync(observationGate: gate);
        await AssignLinearTemplateAsync(host.Services, 1, new[] { "node-a", "done" });
        var vm = CreateLibrary(host.Services);
        await vm.RefreshAsync(); await vm.StartAssignedRunAsync();
        Assert.NotNull(vm.ObservedRun);
        Assert.True(vm.ActivityMonitor.HasRun);
        gate.Arm();
        try
        {
            vm.SelectedVersion = vm.Versions.Single(version => version.Id == VersionOneId);
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(vm.ObservedRun);
            Assert.Empty(vm.ObservedProjections);
            Assert.False(vm.ActivityMonitor.HasRun);
            Assert.Empty(vm.ActivityMonitor.Nodes);
        }
        finally { gate.Open(); await vm.ObserveActiveRunAsync(); }
    }
}
