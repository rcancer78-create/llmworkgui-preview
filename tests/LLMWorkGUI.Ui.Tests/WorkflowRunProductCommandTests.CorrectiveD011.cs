using System;
using System.Text;
using System.Threading.Tasks;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowRunProductCommandTests
{
    [Fact]
    public async Task CorrectiveD011_UnchangedSelectionReviewRefusalDoesNotInventSelectionDrift()
    {
        // Real composed SQLite admission/refusal; the shipped channel has no productive route support.
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind,
            requiredReviewerRoles: new[] { "Reviewer" },
            roleBindings: new[] { new RoleBindingDefinition("Reviewer", "route-opencode", modelId: "model-1") });
        var vm = CreateLibrary(host.Services);
        await vm.RefreshAsync(); await vm.StartAssignedRunAsync();
        vm.StageArtifactPath = WriteArtifactFile("review-d011.md", Encoding.UTF8.GetBytes("the document"));
        await vm.AttachStageArtifactAsync();
        Assert.True(vm.CanRequestAssignedReview);
        var project = vm.SelectedProject!.Id;
        var package = vm.SelectedPackage!.Id;
        var version = vm.SelectedVersion!.Id;

        await vm.RequestAssignedReviewAsync();

        Assert.True(vm.AssignedReviewResult!.IsRefusal);
        Assert.Equal(project, vm.SelectedProject!.Id);
        Assert.Equal(package, vm.SelectedPackage!.Id);
        Assert.Equal(version, vm.SelectedVersion!.Id);
        Assert.DoesNotContain("Выбор на экране изменился", vm.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("Вердикт этим действием не создаётся", vm.StatusMessage, StringComparison.Ordinal);
    }
}
