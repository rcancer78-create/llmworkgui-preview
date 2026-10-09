using System.IO;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless tests of the Phase 10E Workflow Studio panel: template editing and versioning, the separate
/// editable schema vs the actual run graph, document drafts with version/hash, the secret-scan preview,
/// separate reviewer verdicts, the pre-coder gate and the account-context switch.
/// </summary>
public sealed partial class WorkflowStudioViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatingANonLatestVersion_SelectsTheVersionActuallyCreated()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshTemplatesAsync();
        viewModel.NewTemplateId = "review-version-selection";
        viewModel.NewTemplateName = "Review version selection";
        await viewModel.CloneTemplateAsync();
        viewModel.NewTemplateVersion = 3;
        await viewModel.CreateTemplateVersionAsync();
        viewModel.SelectedTemplate = viewModel.Templates.Single(t =>
            t.TemplateId == "review-version-selection" && t.Version == 1);
        viewModel.NewTemplateVersion = 2;
        await viewModel.CreateTemplateVersionAsync();
        Assert.Equal(2, viewModel.SelectedTemplate!.Version);
        Assert.Equal("review-version-selection", viewModel.SelectedTemplate.TemplateId);
        Assert.Contains(viewModel.Templates, t => t.TemplateId == "review-version-selection" && t.Version == 3);
    }

    [Fact]
    public async Task RefreshTemplates_LoadsBuiltInSchemaSeparatelyFromTheEmptyRunGraph()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        Assert.True(viewModel.IsStudioAvailable);
        Assert.NotEmpty(viewModel.Templates);
        Assert.True(viewModel.SelectedTemplate!.IsBuiltIn);
        Assert.Equal(
            viewModel.SelectedTemplate.Definition.Graph.Nodes.Count,
            viewModel.TemplateNodes.Count);
        Assert.NotEmpty(viewModel.TemplateNodes);
        Assert.Empty(viewModel.RunNodes);
        Assert.False(viewModel.HasRunGraph);
        Assert.Equal(WorkflowStudioViewModel.NoRunGraphMessage, viewModel.RunGraphSummary);
    }

    [Fact]
    public async Task EditingTemplateNodes_ChangesTheEditableSchemaOnly()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        var originalCount = viewModel.TemplateNodes.Count;

        viewModel.AddTemplateNode();

        Assert.Equal(originalCount + 1, viewModel.TemplateNodes.Count);
        Assert.Empty(viewModel.RunNodes);
        Assert.Contains(viewModel.TemplateNodes, node => node.NodeId == "node-1");
    }

    [Fact]
    public async Task ValidateTemplateGraph_ReportsValidityOfTheEditedSchema()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        var validReport = await viewModel.ValidateTemplateAsync();

        Assert.True(validReport.IsValid);
        Assert.True(viewModel.IsTemplateGraphValid);
        Assert.True(viewModel.HasTemplateValidation);

        viewModel.TemplateNodes.Clear();

        var invalidReport = await viewModel.ValidateTemplateAsync();

        Assert.False(invalidReport.IsValid);
        Assert.False(viewModel.IsTemplateGraphValid);
        Assert.Contains("Схема невалидна", viewModel.TemplateValidationStatusDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloneTemplate_AddsAUserOwnedCopyAndKeepsTheBuiltInSource()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        viewModel.NewTemplateId = "ui-custom-workflow";
        viewModel.NewTemplateName = "UI custom workflow";

        await viewModel.CloneTemplateAsync();

        var clone = viewModel.Templates.Single(template => template.TemplateId == "ui-custom-workflow");

        Assert.False(clone.IsBuiltIn);
        Assert.Equal(1, clone.Version);
        Assert.Same(clone, viewModel.SelectedTemplate);
        Assert.Contains(
            viewModel.Templates,
            template => template.IsBuiltIn
                && template.TemplateId == WorkflowStudioService.StandardTemplateId
                && template.Version == 1);
    }

    [Fact]
    public async Task CreateTemplateVersion_AddsANewUserOwnedVersionWithoutChangingVersionOne()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        viewModel.NewTemplateId = "ui-versioned-workflow";
        viewModel.NewTemplateName = "UI versioned workflow";
        await viewModel.CloneTemplateAsync();

        viewModel.NewTemplateVersion = 2;

        await viewModel.CreateTemplateVersionAsync();

        Assert.Contains(
            viewModel.Templates,
            template => template.TemplateId == "ui-versioned-workflow" && template.Version == 1);
        Assert.Contains(
            viewModel.Templates,
            template => template.TemplateId == "ui-versioned-workflow" && template.Version == 2);
    }

    [Fact]
    public async Task GenerateAndUpdateDraft_ChangesVersionAndHashAndResetsVerdicts()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        viewModel.SelectedDocumentTemplate = viewModel.DocumentTemplates.Single(
            option => option.Kind == DocumentTemplateKind.ProblemStatement);
        viewModel.DraftTitle = "Проблема UI";

        var draft = await viewModel.GenerateDraftAsync();
        var originalHash = draft.ContentHash;

        Assert.True(viewModel.HasDraft);
        Assert.Equal(originalHash, viewModel.DraftHashDisplay);
        Assert.Equal("v1", viewModel.DraftVersionDisplay);
        Assert.True(viewModel.IsDocumentComplete);

        await viewModel.AddReviewVerdictAsync();

        Assert.True(viewModel.HasReviewerVerdicts);
        Assert.Single(viewModel.ReviewerVerdicts);

        viewModel.DraftContent = "## Контекст\n\nручная правка без остальных секций";

        await viewModel.UpdateDraftAsync();

        Assert.Equal(2, viewModel.CurrentDraft!.Version);
        Assert.Equal("v2", viewModel.DraftVersionDisplay);
        Assert.NotEqual(originalHash, viewModel.DraftHashDisplay);
        Assert.Empty(viewModel.ReviewerVerdicts);
        Assert.False(viewModel.IsDocumentComplete);
    }

    [Fact]
    public async Task PreviewDraft_WithSecretFinding_IsBlockedAndRedacted()
    {
        var harness = new StudioHarness();

        harness.Scanner.Report = new WorkflowSecretScanReport(
            true,
            new[] { new WorkflowSecretFinding("drafts/ProblemStatement.md", 3, "TestSecret", "[redacted]") },
            new[] { "drafts/ProblemStatement.md" });

        var viewModel = harness.CreateViewModel();

        await viewModel.GenerateDraftAsync();
        await viewModel.PreviewDraftAsync();

        Assert.True(viewModel.HasPreview);
        Assert.True(viewModel.PreviewIsBlocked);
        Assert.Contains("[REDACTED:TestSecret]", viewModel.PreviewContent, StringComparison.Ordinal);
        Assert.Contains("1 finding", viewModel.PreviewScanSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewDraft_WithoutScanner_IsBlockedFailClosed()
    {
        var time = new FakeTimeProvider { UtcNow = Now };
        var documentsWithoutScanner = new DocumentTemplateService(
            secretScanner: null,
            time,
            () => "draft-no-scanner");
        var viewModel = new WorkflowStudioViewModel(
            new WorkflowStudioService(timeProvider: time),
            documentsWithoutScanner,
            new PreCoderGateValidator(),
            time);

        await viewModel.GenerateDraftAsync();
        await viewModel.PreviewDraftAsync();

        Assert.True(viewModel.HasPreview);
        Assert.True(viewModel.PreviewIsBlocked);
        Assert.Empty(viewModel.PreviewContent);
        Assert.Contains("сканер не сконфигурирован", viewModel.PreviewScanSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCoderGate_BlocksCoderStartUntilEveryDocumentIsReviewedApprovedAndRouteProven()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RequestCoderStartAsync();

        Assert.False(viewModel.IsCoderStartAllowed);
        Assert.Equal(0, viewModel.CoderStartCount);
        Assert.True(viewModel.HasBlocker);
        Assert.Contains(WorkflowStudioViewModel.CoderTransitionId, viewModel.Blocker, StringComparison.Ordinal);

        foreach (var option in viewModel.DocumentTemplates)
        {
            viewModel.SelectedDocumentTemplate = option;
            await viewModel.GenerateDraftAsync();

            foreach (var role in WorkflowStudioDocumentRules.RequiredReviewerRoles)
            {
                viewModel.ReviewerRole = role;
                viewModel.ReviewerVerdict = WorkflowReviewVerdict.Approve;
                await viewModel.AddReviewVerdictAsync();
            }

            await viewModel.ApproveDocumentAsync();
        }

        viewModel.GateRequestedRouteId = "route-opencode";
        viewModel.GateObservedRouteId = "route-opencode";

        await viewModel.RefreshGateAsync();

        Assert.True(viewModel.IsCoderStartAllowed);
        Assert.All(viewModel.GateChecks, check => Assert.True(check.IsSatisfied, check.CheckId));
        Assert.Equal("Кодер может стартовать", viewModel.CoderStartStatusDisplay);

        await viewModel.RequestCoderStartAsync();

        Assert.Equal(1, viewModel.CoderStartCount);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public async Task PreCoderGate_ConflictingVerdictsAreRoutedToTheNamedResolutionNode()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.GenerateDraftAsync();

        viewModel.ReviewerRole = "Reviewer";
        viewModel.ReviewerVerdict = WorkflowReviewVerdict.Approve;
        await viewModel.AddReviewVerdictAsync();

        viewModel.ReviewerVerdict = WorkflowReviewVerdict.Reject;
        await viewModel.AddReviewVerdictAsync();

        viewModel.GateEscalationTargetNodeId = "stage-conflict-resolution";

        await viewModel.RefreshGateAsync();

        Assert.False(viewModel.IsCoderStartAllowed);
        Assert.True(viewModel.GateRequiresEscalation);
        Assert.Contains(
            "stage-conflict-resolution",
            viewModel.GateEscalationDisplay,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRun_ShowsTheActualReadOnlyRunGraphNextToTheEditableTemplateSchema()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var run = WorkflowRun.Start(
            "run-ui",
            "project-ui",
            "pkg-ui",
            "ver-ui",
            "session-ui",
            scheme.GetRequiredStage(scheme.InitialStageId),
            Now);

        // The first stage of the standard scheme is gated by the artifact it produced, so the run records
        // the problem statement before it moves on to the architecture stage.
        run = run.WithArtifact(new WorkflowArtifactEvidence(
            "artifact-ui",
            "run-ui",
            WorkflowScheme.TaskSpecificationStageId,
            "TaskSpecificationDocument",
            "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222",
            "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222",
            Now.AddMinutes(2),
            64,
            DataClassification.PrivateSource));

        run.AdvanceTo(
            scheme.GetRequiredStage(WorkflowScheme.TaskSpecificationStageId),
            scheme.GetRequiredStage(WorkflowScheme.ArchitectureStageId),
            "Problem statement delivered",
            Now.AddMinutes(5));

        viewModel.LoadRun(run, scheme);

        Assert.True(viewModel.HasRunGraph);
        Assert.Equal(2, viewModel.RunNodes.Count);
        Assert.Contains(
            viewModel.RunNodes,
            node => node.StageId == WorkflowScheme.ArchitectureStageId && node.IsCurrent);
        Assert.Contains("run-ui", viewModel.RunGraphSummary, StringComparison.Ordinal);

        // The editable template schema is a separate surface and was not replaced by the run graph.
        Assert.Equal(
            viewModel.SelectedTemplate!.Definition.Graph.Nodes.Count,
            viewModel.TemplateNodes.Count);
    }

    [Fact]
    public async Task SwitchAgyProfile_RefusesWithTheNamedMissingProofAndKeepsMirasimStateUntouched()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();
        var mirasim = new WorkflowMirasimIsolationState("mirasim-active-account", "recording", true);

        var result = await viewModel.SwitchAgyProfileAsync(
            "profile-alpha",
            "route-star-cliproxy-agy",
            previousNativeSessionId: "native-old",
            mirasimState: mirasim);

        // The Studio helper no longer takes an observed route, and the switch is refused: no native
        // session is invented locally and the previous session is preserved, not forked.
        Assert.False(result.IsSwitched);
        Assert.True(result.IsBlocked);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal("native-old", result.PreviousNativeSessionId);
        Assert.True(result.RequiresNewNativeSession);
        Assert.False(result.CarriesPreviousSession);
        Assert.False(result.CredentialsTransferred);
        Assert.Same(mirasim, result.MirasimState);
        Assert.Contains("no native session is created", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchAgyProfile_HasNoParameterThroughWhichACallerCanSupplyAnObservedRoute()
    {
        var observedRouteParameter = typeof(WorkflowStudioViewModel)
            .GetMethod(nameof(WorkflowStudioViewModel.SwitchAgyProfileAsync))!
            .GetParameters()
            .Where(parameter => string.Equals(
                parameter.Name,
                "observedRouteId",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(observedRouteParameter);

        var sameParameter = typeof(WorkflowStudioViewModel)
            .GetMethod(nameof(WorkflowStudioViewModel.SwitchCodexHomeAsync))!
            .GetParameters()
            .Where(parameter => string.Equals(
                parameter.Name,
                "observedRouteId",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(sameParameter);
    }

    [Fact]
    public async Task SwitchCodexHome_RefusesWithTheNamedMissingProofAndHidesTheHomePath()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();
        var codexHome = Path.Combine(Path.GetTempPath(), "codex-home");

        var result = await viewModel.SwitchCodexHomeAsync(
            "codex-account",
            codexHome,
            "route-star-cliproxy-codex");

        Assert.False(result.IsSwitched);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.DoesNotContain(codexHome, result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_ExecuteTheStudioOperationsAndReflectValidationState()
    {
        var harness = new StudioHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshTemplatesAsync();

        Assert.True(viewModel.ValidateTemplateCommand.CanExecute(null));
        viewModel.ValidateTemplateCommand.Execute(null);
        Assert.True(viewModel.HasTemplateValidation);
        Assert.True(viewModel.IsTemplateGraphValid);

        viewModel.NewTemplateId = "command-template";
        viewModel.NewTemplateName = "Command template";
        Assert.True(viewModel.CloneTemplateCommand.CanExecute(null));
        viewModel.CloneTemplateCommand.Execute(null);
        Assert.Contains(viewModel.Templates, template => template.TemplateId == "command-template");

        Assert.True(viewModel.GenerateDraftCommand.CanExecute(null));
        viewModel.GenerateDraftCommand.Execute(null);

        var draft = viewModel.CurrentDraft;
        Assert.NotNull(draft);
        Assert.True(viewModel.UpdateDraftCommand.CanExecute(null));
        Assert.True(viewModel.AddReviewVerdictCommand.CanExecute(null));

        viewModel.DraftContent = "## Контекст\n\nправка через команду";
        viewModel.UpdateDraftCommand.Execute(null);

        Assert.Equal(2, draft!.Version);

        viewModel.AddReviewVerdictCommand.Execute(null);

        Assert.True(viewModel.HasReviewerVerdicts);

        Assert.True(viewModel.RefreshGateCommand.CanExecute(null));
        viewModel.RefreshGateCommand.Execute(null);

        Assert.True(viewModel.HasGateResult);
        Assert.False(viewModel.IsCoderStartAllowed);
        Assert.True(viewModel.RequestCoderStartCommand.CanExecute(null));

        viewModel.RequestCoderStartCommand.Execute(null);

        Assert.Equal(0, viewModel.CoderStartCount);
        Assert.True(viewModel.HasBlocker);
    }

    [Fact]
    public async Task WorkflowLibraryViewModel_ExposesTheStudioPanel()
    {
        var harness = new StudioHarness();

        var library = new WorkflowLibraryViewModel(
            studioService: harness.Studio,
            documentTemplateService: harness.Documents,
            preCoderGateValidator: new PreCoderGateValidator());

        await library.Studio.RefreshTemplatesAsync();

        Assert.True(library.IsStudioAvailable);
        Assert.NotNull(library.Studio);
        Assert.NotEmpty(library.Studio.Templates);
        Assert.True(library.Studio.SelectedTemplate!.IsBuiltIn);
    }

    private sealed class StudioHarness
    {
        private int _draftSequence;

        public StudioHarness()
        {
            Time = new FakeTimeProvider { UtcNow = Now };
            Scanner = new RecordingSecretScanner();

            Studio = new WorkflowStudioService(
                graphValidator: new WorkflowGraphValidator(),
                templateStore: new InMemoryWorkflowTemplateStore(),
                timeProvider: Time);

            Documents = new DocumentTemplateService(
                Scanner,
                Time,
                () => "draft-" + (++_draftSequence),
                new SyntheticStudioApprovalIdentity());
        }

        public FakeTimeProvider Time { get; }

        public RecordingSecretScanner Scanner { get; }

        public WorkflowStudioService Studio { get; }

        public DocumentTemplateService Documents { get; }

        public WorkflowStudioViewModel CreateViewModel() =>
            new(Studio, Documents, new PreCoderGateValidator(), Time);
    }

    private sealed class RecordingSecretScanner : IWorkflowSecretScanner
    {
        public WorkflowSecretScanReport Report { get; set; } = WorkflowSecretScanReport.Empty;

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The preview only scans in-memory drafts.");

        public Task<WorkflowSecretScanReport> ScanFilesAsync(
            IReadOnlyDictionary<string, byte[]> files,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);
    }
}
