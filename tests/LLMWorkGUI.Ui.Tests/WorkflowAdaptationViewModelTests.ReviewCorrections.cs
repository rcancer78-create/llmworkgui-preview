using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowAdaptationViewModelTests
{
    [Fact]
    public async Task Review_FailedPreviewCannotAuthorizeStartOrForgetOperatorExclusions()
    {
        var harness = new AdaptationHarness();
        var vm = harness.CreateViewModel();
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        vm.PreSendFiles.Single(file => file.RelativePath == "README.md").IsExcluded = true;
        harness.Adaptation.InitialFailure = new IOException("synthetic preview unavailable");

        await vm.RefreshPreSendPreviewAsync();
        harness.Adaptation.InitialFailure = null;
        await vm.StartAdaptationAsync();

        Assert.Empty(harness.Adaptation.StartRequests);
        Assert.False(vm.CanStartAdaptation);
        Assert.Contains(vm.PreSendFiles, file => file.RelativePath == "README.md" && file.IsExcluded);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("scope")]
    [InlineData("exclusion")]
    public async Task Review_ChangedInputsRequireANewSuccessfulPreviewBeforeStart(string input)
    {
        var harness = new AdaptationHarness();
        var vm = harness.CreateViewModel();
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        if (input == "goal") vm.SelectedGoal = AdaptationGoal.Quality;
        else if (input == "scope") vm.AllowExpandedSemanticScope = true;
        else vm.PreSendFiles.Single(file => file.RelativePath == "README.md").IsExcluded = true;

        await vm.StartAdaptationAsync();

        Assert.Empty(harness.Adaptation.StartRequests);
        Assert.False(vm.CanStartAdaptation);
        // This is preview consent, not proof of a native backend dispatch or a scanner bypass.
    }

    [Fact]
    public async Task Review_RefreshingPreviewRetainsTheExplicitUserExcludedPaths()
    {
        var harness = new AdaptationHarness();
        var vm = harness.CreateViewModel();
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        vm.PreSendFiles.Single(file => file.RelativePath == "README.md").IsExcluded = true;

        await vm.RefreshPreSendPreviewAsync();

        Assert.Contains("README.md", harness.Adaptation.LastPreviewExcludedFiles);
        Assert.True(vm.PreSendFiles.Single(file => file.RelativePath == "README.md").IsExcluded);
    }

    [Fact]
    public async Task Review_DirectConcurrentAcceptCannotSaveOrActivateTheCandidateTwice()
    {
        var harness = new AdaptationHarness();
        var vm = harness.CreateViewModel();
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await vm.StartAdaptationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SaveCandidateVersionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Adaptation.SaveHandler = (_, _) => { entered.TrySetResult(); return release.Task; };
        var first = vm.AcceptAndActivateAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = vm.AcceptAndActivateAsync();
        var result = new SaveCandidateVersionResult("ver-candidate", "pkg-1", 2, HashA,
            WorkflowSourceType.SyntheticDraft, Now, null, Array.Empty<AdaptationBlockerKind>());
        try
        {
            Assert.Single(harness.Adaptation.SavedSessions);
        }
        finally
        {
            release.TrySetResult(result);
            await Task.WhenAll(first, second);
        }
        Assert.Single(harness.Activation.ActivationRequests);
    }
}
