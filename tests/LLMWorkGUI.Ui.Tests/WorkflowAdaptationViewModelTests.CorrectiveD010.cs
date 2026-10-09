using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowAdaptationViewModelTests
{
    [Fact]
    public async Task CorrectiveD010_CancelledOpenCannotPublishLateRoutesOrAuthorizeHiddenStart()
    {
        var harness = new AdaptationHarness();
        var catalog = new CorrectiveD010HeldCatalog(harness.Catalog.PreviewCatalog);
        var vm = new WorkflowAdaptationViewModel(harness.Adaptation, harness.Activation, catalog);
        var opening = vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await catalog.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(vm.IsVisible);
            await vm.CancelAsync();
            Assert.False(vm.IsVisible);
        }
        finally { catalog.Release.TrySetResult(); await opening; }

        await vm.StartAdaptationAsync();

        Assert.Empty(harness.Adaptation.StartRequests);
        Assert.Null(harness.Adaptation.LastPreviewRouteId);
        Assert.Null(vm.SelectedRoute);
        Assert.Empty(vm.AvailableRoutes);
        Assert.Empty(vm.PromptPreview);
        Assert.False(vm.CanStartAdaptation);
    }

    [Theory]
    [InlineData("route", false)]
    [InlineData("goal", false)]
    [InlineData("scope", false)]
    [InlineData("exclusion", false)]
    [InlineData("route", true)]
    [InlineData("goal", true)]
    [InlineData("scope", true)]
    [InlineData("exclusion", true)]
    public async Task CorrectiveD010_CapturedTurnInputsStayStableDuringDispatchAndActiveSession(string input, bool activeSession)
    {
        var harness = new AdaptationHarness();
        harness.Catalog.PreviewCatalog = new SanitizedCapabilityCatalog(Array.Empty<SanitizedProviderInfo>(),
            new[] {
                CreateCatalogModel("model-1", "Primary", "prov-1", BackendType.OpenCode, NativeModelId, 128000, true),
                CreateCatalogModel("model-2", "Second", "prov-1", BackendType.OpenCode, "opencode/other-model", 128000, true)
            }, Now);
        var service = new CorrectiveD010HeldStart(harness.Adaptation);
        var vm = new WorkflowAdaptationViewModel(service, harness.Activation, harness.Catalog);
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        Assert.True(vm.CanStartAdaptation);
        var route = vm.SelectedRoute;
        var goal = vm.SelectedGoal;
        var expanded = vm.AllowExpandedSemanticScope;
        var file = vm.PreSendFiles.Single(item => item.RelativePath == "README.md");
        var excluded = file.IsExcluded;
        var starting = vm.StartAdaptationAsync();
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (activeSession) { service.Release.TrySetResult(); await starting; }
        try
        {
            if (activeSession) Assert.True(vm.IsSessionActive);
            else Assert.True(vm.IsBusy);
            if (input == "route") vm.SelectedRoute = vm.AvailableRoutes.Single(item => !ReferenceEquals(item, route));
            else if (input == "goal") vm.SelectedGoal = AdaptationGoal.Quality;
            else if (input == "scope") vm.AllowExpandedSemanticScope = !expanded;
            else file.IsExcluded = !excluded;

            Assert.Same(route, vm.SelectedRoute);
            Assert.Equal(goal, vm.SelectedGoal);
            Assert.Equal(expanded, vm.AllowExpandedSemanticScope);
            Assert.Equal(excluded, file.IsExcluded);
            Assert.Single(harness.Adaptation.StartRequests);
        }
        finally { service.Release.TrySetResult(); await starting; }
    }

    private sealed class CorrectiveD010HeldCatalog(SanitizedCapabilityCatalog catalog) : ISanitizedCatalogProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; return catalog; }
    }

    private sealed class CorrectiveD010HeldStart(IWorkflowAdaptationService inner) : IWorkflowAdaptationService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(string id, string route, AdaptationGoal goal,
            IReadOnlyList<string>? excluded = null, bool expanded = false, CancellationToken token = default) =>
            inner.PreparePreSendPreviewAsync(id, route, goal, excluded, expanded, token);
        public async Task<AdaptationCandidateResult> StartAdaptationAsync(AdaptationExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.StartAdaptationAsync(request, cancellationToken);
            Entered.TrySetResult(); await Release.Task; return result;
        }
        public Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(AdaptationFollowUpRequest request,
            CancellationToken token = default) => inner.SubmitFollowUpTurnAsync(request, token);
        public Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(string id, CancellationToken token = default) =>
            inner.SaveCandidateVersionAsync(id, token);
        public Task DiscardSessionAsync(string id, CancellationToken token = default) => inner.DiscardSessionAsync(id, token);
        public AdaptationSessionSnapshot GetSessionSnapshot(string id) => inner.GetSessionSnapshot(id);
    }
}
