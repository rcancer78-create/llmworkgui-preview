using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.Domain.Entities;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowLibraryViewModelTests
{
    [Fact]
    public async Task Review_ChangingTheActiveVersionDoesNotReplayACachedRoutePolicyOverANewerPolicy()
    {
        var harness = CreateLibrary();
        harness.AddPackage("pkg-1", HashA);
        harness.AddVersion("pkg-1", "ver-1", 1, HashA);
        harness.AddVersion("pkg-1", "ver-2", 2, HashB);
        harness.AddProject("project-a");
        var binding = new WorkflowBinding(Guid.NewGuid().ToString(), "project-a", "pkg-1", "ver-1", "policy-old", Now, Now);
        await harness.Bindings.UpsertAsync(binding);
        var vm = harness.CreateViewModel(); await vm.RefreshAsync();
        vm.SelectedVersion = vm.Versions.Single(version => version.Id == "ver-2");
        await harness.Bindings.UpsertAsync(new WorkflowBinding(binding.Id, "project-a", "pkg-1", "ver-1",
            "policy-new", Now, Now.AddSeconds(1)));

        await vm.SetActiveVersionAsync();

        var stored = await harness.Bindings.GetByProjectAndPackageAsync("project-a", "pkg-1");
        Assert.Equal("ver-2", stored!.ActiveVersionId);
        Assert.Equal("policy-new", stored.RoutePolicyId);
    }

    [Fact]
    public async Task Review_LateBindingRowsCannotBeRelabeledAsTheNewProject()
    {
        var harness = CreateLibrary();
        harness.AddPackage("pkg-1", HashA);
        harness.AddVersion("pkg-1", "ver-1", 1, HashA);
        harness.AddProject("project-a"); harness.AddProject("project-b");
        var a = new WorkflowBinding(Guid.NewGuid().ToString(), "project-a", "pkg-1", "ver-1", null, Now, Now);
        var b = new WorkflowBinding(Guid.NewGuid().ToString(), "project-b", "pkg-1", "ver-1", null, Now, Now);
        await harness.Bindings.UpsertAsync(a); await harness.Bindings.UpsertAsync(b);
        var vm = harness.CreateViewModel(); await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<WorkflowBinding>>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Bindings.ListHandler = id =>
        {
            if (id == "project-a") { entered.TrySetResult(); return release.Task; }
            return Task.FromResult<IReadOnlyList<WorkflowBinding>>(new[] { b });
        };
        var refresh = vm.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.SelectedProject = vm.Projects.Single(project => project.Id == "project-b");
        }
        finally { release.TrySetResult(new[] { a }); await refresh; }

        Assert.NotEmpty(vm.Bindings);
        Assert.All(vm.Bindings, row => Assert.Equal("project-b", row.ProjectIdDisplay));
        var current = Assert.Single(vm.Bindings);
        Assert.Equal(b.Id, current.Binding.Id);
        Assert.Equal("project-b", current.Binding.ProjectId);
        Assert.DoesNotContain(vm.Bindings, row => row.Binding.Id == a.Id);
    }

    [Fact]
    public async Task Review_LateVersionBindingCannotRestoreAnActiveFlagFromThePreviousProject()
    {
        var harness = CreateLibrary();
        harness.AddPackage("pkg-1", HashA); harness.AddVersion("pkg-1", "ver-1", 1, HashA);
        harness.AddProject("project-a"); harness.AddProject("project-b");
        var a = new WorkflowBinding(Guid.NewGuid().ToString(), "project-a", "pkg-1", "ver-1", null, Now, Now);
        await harness.Bindings.UpsertAsync(a);
        var vm = harness.CreateViewModel(); await vm.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource<WorkflowBinding?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<IReadOnlyList<WorkflowBinding>>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Bindings.GetHandler = (id, _) =>
        {
            if (id == "project-a") { entered.TrySetResult(); return releaseA.Task; }
            return Task.FromResult<WorkflowBinding?>(null);
        };
        harness.Bindings.ListHandler = id => id == "project-b" ? releaseB.Task
            : Task.FromResult<IReadOnlyList<WorkflowBinding>>(new[] { a });
        var loadA = vm.LoadVersionsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.SelectedProject = vm.Projects.Single(project => project.Id == "project-b");
            releaseA.TrySetResult(a); await loadA;
            Assert.False(vm.HasCurrentBinding);
            Assert.DoesNotContain(vm.Versions, version => version.IsActiveInCurrentProject);
        }
        finally
        {
            releaseA.TrySetResult(a);
            releaseB.TrySetResult(Array.Empty<WorkflowBinding>());
            await loadA;
            await vm.LoadVersionsAsync();
        }
    }
}
