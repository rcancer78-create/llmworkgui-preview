using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowStudioViewModelTests
{
    [Fact]
    public void Review_NodeEditorRoundTripPreservesEveryNonEditableDeclaredField()
    {
        var node = new WorkflowNodeDefinition("prompt", WorkflowNodeKind.Prompt, "Prompt", "Executor",
            new[] { "Chat" }, "route-primary", new[] { "route-fallback" }, TimeSpan.FromSeconds(40), 2,
            "done", "failed", "declared-condition", "artifact-v1", "read-only",
            new WorkflowNodeGateMetadata(WorkflowStageKind.Custom, new[] { "Reviewer" }, true, "artifact-v1"));
        var original = new WorkflowGraph("prompt", new[] { node });
        var roundTrip = new WorkflowGraph("prompt", new[] { LLMWorkGUI.App.ViewModels.WorkflowNodeEditorViewModel.FromDefinition(node).ToDefinition() });
        Assert.Equal(WorkflowGraphSnapshot.Serialize(original), WorkflowGraphSnapshot.Serialize(roundTrip));
    }

    [Fact]
    public async Task Review_SavingAnUneditedBuiltInGraphPreservesItsCanonicalDeclaredMetadata()
    {
        var harness = new StudioHarness();
        var vm = harness.CreateViewModel(); await vm.RefreshTemplatesAsync();
        var original = vm.SelectedTemplate!.Definition.Graph;
        var targetId = vm.NewTemplateId; var targetVersion = vm.NewTemplateVersion;
        await vm.SaveTemplateAsync();
        Assert.False(vm.HasBlocker);
        var saved = await harness.Studio.GetRequiredTemplateAsync(targetId, targetVersion);
        Assert.Equal(WorkflowGraphSnapshot.Serialize(original), WorkflowGraphSnapshot.Serialize(saved.Graph));
    }

    [Theory]
    [InlineData("approve", false)]
    [InlineData("review", false)]
    [InlineData("preview", false)]
    [InlineData("approve", true)]
    [InlineData("review", true)]
    [InlineData("preview", true)]
    public async Task Review_DirtyEditorCannotApproveReviewOrPreviewDifferentPersistedBytes(string action, bool commandOnly)
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();
        await viewModel.GenerateDraftAsync();
        var draft = viewModel.CurrentDraft!;
        var persistedHash = draft.ContentHash;
        var unsaved = draft.Content + "\nunsaved editor changes";
        viewModel.DraftContent = unsaved;
        ICommand command = action switch
        {
            "approve" => viewModel.ApproveDocumentCommand,
            "review" => viewModel.AddReviewVerdictCommand,
            _ => viewModel.PreviewDraftCommand
        };
        if (commandOnly)
        {
            Assert.False(command.CanExecute(null));
            return;
        }

        await (action switch
        {
            "approve" => viewModel.ApproveDocumentAsync(),
            "review" => viewModel.AddReviewVerdictAsync(),
            _ => viewModel.PreviewDraftAsync()
        });

        Assert.Null(draft.UserApproval);
        Assert.Empty(draft.ReviewerVerdicts);
        Assert.False(viewModel.HasPreview);
        Assert.Equal(persistedHash, draft.ContentHash);
        Assert.Equal(unsaved, viewModel.DraftContent);
        Assert.True(viewModel.HasBlocker);
    }

    [Fact]
    public async Task Review_DirtyEditorInvalidatesCachedPlanningAllowanceAndCannotStartTheDemonstration()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();
        await PopulateApprovedStudioDocumentsAsync(viewModel);
        await viewModel.RefreshGateAsync();
        Assert.True(viewModel.IsCoderStartAllowed);

        viewModel.DraftContent += "\nunsaved decision-changing edit";

        Assert.False(viewModel.HasGateResult);
        Assert.False(viewModel.IsCoderStartAllowed);
        await viewModel.RequestCoderStartAsync();
        Assert.Equal(0, viewModel.CoderStartCount);
        Assert.True(viewModel.HasBlocker);
        // This count is the Studio planning demonstration, not native coder dispatch evidence.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_VisualFlagsBelongToTheirExactDraftAndCannotMigrateThroughAnOptionalDocument(bool optionalOwnsFlags)
    {
        var harness = new StudioHarness();
        var original = harness.Studio.GetBuiltInTemplates()[0];
        await harness.Studio.SaveTemplateAsync(new WorkflowTemplateDefinition("one-required", 1, "One required",
            "Explicit planning document subset", original.Graph, original.RoleBindings,
            new[] { DocumentTemplateKind.ProblemStatement }, false, Now));
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshTemplatesAsync();
        viewModel.SelectedTemplate = viewModel.Templates.Single(template => template.TemplateId == "one-required");
        viewModel.SelectedDocumentTemplate = viewModel.DocumentTemplates.Single(option => option.Kind == DocumentTemplateKind.ProblemStatement);
        await viewModel.GenerateDraftAsync();
        foreach (var role in WorkflowStudioDocumentRules.RequiredReviewerRoles)
        {
            viewModel.ReviewerRole = role;
            await viewModel.AddReviewVerdictAsync();
        }
        await viewModel.ApproveDocumentAsync();
        viewModel.UiArtifactPresent = !optionalOwnsFlags;
        viewModel.VisualAcceptancePresent = !optionalOwnsFlags;
        viewModel.SelectedDocumentTemplate = viewModel.DocumentTemplates.Single(option => option.Kind == DocumentTemplateKind.Architecture);
        await viewModel.GenerateDraftAsync();
        viewModel.UiArtifactPresent = optionalOwnsFlags;
        viewModel.VisualAcceptancePresent = optionalOwnsFlags;
        viewModel.GateRequiresUiArtifact = true;
        viewModel.GateRequiresUserVisualAcceptance = true;

        var result = await viewModel.RefreshGateAsync();

        Assert.Equal(!optionalOwnsFlags, result.IsAllowed);
        Assert.Equal(optionalOwnsFlags, result.HasMissingUiArtifact);
        Assert.Equal(optionalOwnsFlags, result.HasMissingVisualAcceptance);
    }

    [Fact]
    public async Task Review_EditingANodeImmediatelyRetiresItsPreviousSuccessfulValidation()
    {
        var viewModel = new StudioHarness().CreateViewModel();
        await viewModel.RefreshTemplatesAsync();
        await viewModel.ValidateTemplateAsync();
        Assert.True(viewModel.IsTemplateGraphValid);
        viewModel.TemplateNodes[0].SuccessTargetNodeId = "missing-node";

        Assert.False(viewModel.HasTemplateValidation);
        Assert.False(viewModel.IsTemplateGraphValid);
        await viewModel.ValidateTemplateAsync();
        Assert.True(viewModel.HasTemplateValidation);
        Assert.False(viewModel.IsTemplateGraphValid);
    }

    [Fact]
    public async Task Review_ClearingTemplateSelectionRemovesTheOldSelectedNodeAndValidation()
    {
        var viewModel = new StudioHarness().CreateViewModel();
        await viewModel.RefreshTemplatesAsync();
        viewModel.SelectedNode = viewModel.TemplateNodes[0];
        await viewModel.ValidateTemplateAsync();

        viewModel.SelectedTemplate = null;

        Assert.Null(viewModel.SelectedNode);
        Assert.Empty(viewModel.TemplateNodes);
        Assert.False(viewModel.HasTemplateValidation);
        Assert.False(viewModel.IsTemplateGraphValid);
    }

    private static async Task PopulateApprovedStudioDocumentsAsync(WorkflowStudioViewModel viewModel)
    {
        foreach (var option in viewModel.DocumentTemplates)
        {
            viewModel.SelectedDocumentTemplate = option;
            await viewModel.GenerateDraftAsync();
            foreach (var role in WorkflowStudioDocumentRules.RequiredReviewerRoles)
            {
                viewModel.ReviewerRole = role;
                await viewModel.AddReviewVerdictAsync();
            }
            await viewModel.ApproveDocumentAsync();
        }
        viewModel.GateRequestedRouteId = "route-opencode";
        viewModel.GateObservedRouteId = "route-opencode";
    }
}
