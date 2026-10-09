using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowAdaptationViewModelTests
{
    // Synthetic UI projection only; sealed SQLite metadata and preflight are verified in IntegrationTests.
    private sealed class MaterialProjectionStore : IWorkflowMaterialPolicyStore
    {
        public WorkflowMaterialPolicy Current=new("ver-1",HashA,DataClassification.Restricted,0,false);
        public List<(string Version,string Blob,long Revision,DataClassification Class)> Declarations=[];
        public Exception? Failure;
        public Exception? ReadFailure;
        public Task<WorkflowMaterialPolicy> ReadAsync(string versionId,CancellationToken token=default) => ReadFailure is null
            ? Task.FromResult(Current) : Task.FromException<WorkflowMaterialPolicy>(ReadFailure);
        public Task<WorkflowMaterialPolicy> DeclareAsync(string versionId,string blob,long revision,DataClassification classification,CancellationToken token=default)
        {
            if(Failure is not null) return Task.FromException<WorkflowMaterialPolicy>(Failure);
            Declarations.Add((versionId,blob,revision,classification));
            Current=new(versionId,blob,classification,revision+1,true); return Task.FromResult(Current);
        }
    }
    [Fact]
    public async Task UndeclaredVersionDoesNotStartUntilExplicitMaterialDeclaration()
    {
        var h=new AdaptationHarness(); var store=new MaterialProjectionStore();
        var vm=new WorkflowAdaptationViewModel(h.Adaptation,h.Activation,h.Catalog,store);
        await vm.OpenForVersionAsync(h.Version,h.Package,h.Project);
        Assert.False(vm.CanStartAdaptation); Assert.True(vm.CanEditMaterialClassification);
        await vm.StartAdaptationAsync(); Assert.Empty(h.Adaptation.StartRequests); Assert.Empty(store.Declarations);
        vm.MaterialClassification=DataClassification.PrivateSource;
        Assert.False(vm.CanStartAdaptation); // Selecting a label alone never changes stored policy.
        await vm.SaveMaterialClassificationAsync();
        Assert.Equal((h.Version.Id,h.Version.Version.BlobId,0L,DataClassification.PrivateSource),Assert.Single(store.Declarations));
        Assert.True(vm.CanStartAdaptation);
        h.Adaptation.StartResult=CreateCandidateResult("owned-material-session"); await vm.StartAdaptationAsync();
        Assert.Equal(h.Project.Id,Assert.Single(h.Adaptation.StartRequests).ProjectId);
        Assert.Equal(h.Version.Id,h.Adaptation.StartRequests[0].WorkflowVersionId);
        Assert.False(vm.CanEditMaterialClassification);
        await vm.SaveMaterialClassificationAsync(); Assert.Single(store.Declarations);
    }
    [Fact]
    public async Task DeclaredRestrictedVersionRemainsBlocked()
    {
        var h=new AdaptationHarness(); var store=new MaterialProjectionStore
            { Current=new("ver-1",HashA,DataClassification.Restricted,1,true) };
        var vm=new WorkflowAdaptationViewModel(h.Adaptation,h.Activation,h.Catalog,store);
        await vm.OpenForVersionAsync(h.Version,h.Package,h.Project);
        Assert.False(vm.CanStartAdaptation);
        await vm.StartAdaptationAsync(); Assert.Empty(h.Adaptation.StartRequests);
    }
    [Fact]
    public async Task DeclarationFailureDoesNotEnableOrStartAdaptation()
    {
        var h=new AdaptationHarness(); var store=new MaterialProjectionStore { Failure=new WorkflowValidationException("Stored revision changed") };
        var vm=new WorkflowAdaptationViewModel(h.Adaptation,h.Activation,h.Catalog,store);
        await vm.OpenForVersionAsync(h.Version,h.Package,h.Project); vm.MaterialClassification=DataClassification.PublicSource;
        await vm.SaveMaterialClassificationAsync();
        Assert.True(vm.HasError); Assert.False(vm.CanStartAdaptation); Assert.False(vm.IsBusy);
        await vm.StartAdaptationAsync(); Assert.Empty(h.Adaptation.StartRequests);
    }
    [Fact]
    public async Task MetadataReadFailureRemainsVisibleAfterSuccessfulPreviewAndCannotStart()
    {
        var h=new AdaptationHarness(); var store=new MaterialProjectionStore { ReadFailure=new WorkflowValidationException("Material declaration unavailable") };
        var vm=new WorkflowAdaptationViewModel(h.Adaptation,h.Activation,h.Catalog,store);
        await vm.OpenForVersionAsync(h.Version,h.Package,h.Project);
        Assert.True(vm.HasError); Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.DoesNotContain("Material declaration unavailable",vm.ErrorMessage); // Existing safe UI error contract.
        Assert.False(vm.CanStartAdaptation); await vm.StartAdaptationAsync(); Assert.Empty(h.Adaptation.StartRequests);
    }
    [Fact]
    public async Task ReopenedVersionReadsStoredClassAndRevision()
    {
        var h=new AdaptationHarness(); var store=new MaterialProjectionStore { Current=new("ver-1",HashA,DataClassification.PrivateSource,4,true) };
        var vm=new WorkflowAdaptationViewModel(h.Adaptation,h.Activation,h.Catalog,store);
        await vm.OpenForVersionAsync(h.Version,h.Package,h.Project);
        Assert.Equal(DataClassification.PrivateSource,vm.MaterialClassification);
        vm.MaterialClassification=DataClassification.PublicSource; await vm.SaveMaterialClassificationAsync();
        Assert.Equal(4,Assert.Single(store.Declarations).Revision);
    }
}
