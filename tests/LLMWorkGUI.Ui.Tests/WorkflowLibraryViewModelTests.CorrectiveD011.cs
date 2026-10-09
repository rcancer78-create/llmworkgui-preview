using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowLibraryViewModelTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("package")]
    [InlineData("version")]
    public async Task CorrectiveD011_ChangedPickerRetiresTheAcknowledgedActivationTarget(string picker)
    {
        var harness = CreateLibrary();
        harness.AddPackage("pkg-1", HashA); harness.AddPackage("pkg-2", HashB);
        harness.AddVersion("pkg-1", "ver-1", 1, HashA);
        harness.AddVersion("pkg-1", "ver-2", 2, HashB);
        harness.AddVersion("pkg-2", "other-ver", 1, HashC);
        harness.AddProject("project-a"); harness.AddProject("project-b");
        harness.Activation.Validation = CreateBlockedValidation();
        var vm = harness.CreateViewModel(); await vm.RefreshAsync();
        vm.SelectedPackage = vm.Packages.Single(package => package.Id == "pkg-1");
        await vm.LoadVersionsAsync();
        vm.SelectedVersion = vm.Versions.Single(version => version.Id == "ver-1");
        vm.SelectedProject = vm.Projects.Single(project => project.Id == "project-a");
        await vm.BindToProjectAsync();
        Assert.Single(harness.Activation.ActivationRequests);
        foreach (var issue in vm.ActivationBlockers) issue.IsAcknowledged = true;
        Assert.True(vm.CanConfirmActivation);

        if (picker == "project") vm.SelectedProject = vm.Projects.Single(project => project.Id == "project-b");
        else if (picker == "package") vm.SelectedPackage = vm.Packages.Single(package => package.Id == "pkg-2");
        else vm.SelectedVersion = vm.Versions.Single(version => version.Id == "ver-2");
        await vm.ConfirmActivationAsync();

        // Only the original refused admission may exist; no acknowledgement is replayed to that old target.
        Assert.Single(harness.Activation.ActivationRequests);
        Assert.Empty(await harness.Bindings.ListByProjectIdAsync("project-a"));
        Assert.Empty(await harness.Bindings.ListByProjectIdAsync("project-b"));
        Assert.False(vm.CanConfirmActivation);
        Assert.Empty(vm.ActivationBlockers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectiveD011_LatePreviewFailureCannotReplaceTheNewSelectedPreview(bool filePreview)
    {
        var harness = CreateLibrary();
        harness.AddPackage("pkg-1", HashA);
        harness.AddVersion("pkg-1", "ver-1", 1, HashA);
        harness.AddVersion("pkg-1", "ver-2", 2, HashB);
        var preview = new CorrectiveD011HeldPreview();
        var vm = new WorkflowLibraryViewModel(harness.Packages, harness.Versions, harness.Bindings,
            harness.BindingService, previewService: preview);
        await vm.RefreshAsync();
        vm.SelectedVersion = vm.Versions.Single(version => version.Id == "ver-1");
        await vm.LoadPreviewAsync();
        vm.SelectedNode = vm.TreeNodes.Single(node => node.Path == "a.md");
        preview.Arm(filePreview);
        var older = filePreview ? vm.LoadFilePreviewAsync() : vm.LoadPreviewAsync();
        await preview.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (filePreview) vm.SelectedNode = vm.TreeNodes.Single(node => node.Path == "b.md");
            else vm.SelectedVersion = vm.Versions.Single(version => version.Id == "ver-2");
            Assert.Equal(filePreview ? "file:b.md" : "tree:" + HashB,
                filePreview ? vm.SelectedFileContent : vm.DocumentationContent);
        }
        finally { preview.Release.TrySetException(new InvalidOperationException("obsolete-preview-failure")); await older; }

        Assert.Empty(vm.Blocker);
        Assert.Equal(filePreview ? "file:b.md" : "tree:" + HashB,
            filePreview ? vm.SelectedFileContent : vm.DocumentationContent);
    }

    private sealed class CorrectiveD011HeldPreview : IWorkflowPreviewService
    {
        private bool _armed;
        private bool _file;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm(bool file) { _file = file; _armed = true; }
        private async Task HoldAsync(bool file)
        {
            if (!_armed || file != _file) return;
            _armed = false; Entered.TrySetResult(); await Release.Task;
        }
        public async Task<WorkflowTreePreview> GetTreePreviewAsync(string blobId, CancellationToken cancellationToken = default)
        {
            await HoldAsync(false);
            return new(blobId, new[] {
                new WorkflowTreeNode("a.md", "a.md", false, 1, 1, Now),
                new WorkflowTreeNode("b.md", "b.md", false, 1, 1, Now) }, "a.md", "tree:" + blobId, false);
        }
        public async Task<WorkflowFilePreview> GetFilePreviewAsync(string blobId, string relativePath,
            CancellationToken cancellationToken = default)
        {
            await HoldAsync(true);
            return new(blobId, relativePath, 1, false, false, "file:" + relativePath);
        }
    }
}
