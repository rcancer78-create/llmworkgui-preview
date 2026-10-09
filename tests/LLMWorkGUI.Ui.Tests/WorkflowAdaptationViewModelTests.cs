using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the adaptation dialog view model (ТЗ §6.14). The dialog is exercised over
/// stubs, so every assertion is about explicit command semantics: nothing is sent, saved, activated or
/// discarded without the matching user command.
/// </summary>
public sealed partial class WorkflowAdaptationViewModelTests
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Backend-native model id of the seeded OpenCode account; it differs from the account id.</summary>
    private const string NativeModelId = "opencode/primary-model";

    /// <summary>
    /// The route id <see cref="WorkflowAdaptationViewModel"/> must produce for the seeded catalog row.
    /// The literal is spelled out (not recomputed) so a change in the route id encoding is visible here.
    /// </summary>
    private const string OpenCodeRouteId = "route:model-1|prov-1|OpenCode|opencode%2Fprimary-model";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitialFailureShowsPendingCleanupWithoutChangingCancellation(bool preview, bool cancelled)
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();
        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        Exception failure = cancelled ? new OperationCanceledException() : new IOException("synthetic failure");
        failure.Data["ScratchCleanupPending"] = true;
        harness.Adaptation.InitialFailure = failure;
        Func<Task> action = preview ? () => viewModel.RefreshPreSendPreviewAsync() : () => viewModel.StartAdaptationAsync();
        if (cancelled) Assert.Same(failure, await Record.ExceptionAsync(action));
        else await action();
        Assert.Contains("после перезапуска", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsSessionActive);
    }

    [Fact]
    public void WithoutServices_TheDialogIsUnavailable()
    {
        var viewModel = new WorkflowAdaptationViewModel();

        Assert.False(viewModel.IsAdaptationAvailable);
        Assert.False(viewModel.IsActivationAvailable);
    }

    [Fact]
    public async Task OpenForVersion_LoadsRoutableRoutesGoalsAndScannedFiles()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        Assert.True(viewModel.IsVisible);
        Assert.False(viewModel.IsSessionActive);
        Assert.Equal("ver-1", viewModel.SourceVersion!.Id);
        Assert.Equal("pkg-1", viewModel.Package!.Id);

        var route = Assert.Single(viewModel.AvailableRoutes);
        Assert.Equal(OpenCodeRouteId, route.Id);
        Assert.NotEqual("model-1", route.Id);
        Assert.Same(route, viewModel.SelectedRoute);
        Assert.DoesNotContain(viewModel.AvailableRoutes, item => item.Id == "model-disabled");

        Assert.Equal(4, viewModel.AvailableGoals.Count);
        Assert.Contains(AdaptationGoal.Balanced, viewModel.AvailableGoals);
        Assert.Equal(AdaptationGoal.Balanced, viewModel.SelectedGoal);

        Assert.Equal(2, viewModel.PreSendFiles.Count);

        var readme = viewModel.PreSendFiles.Single(file => file.RelativePath == "README.md");
        var secretFile = viewModel.PreSendFiles.Single(file => file.RelativePath == "config/secrets.env");

        Assert.False(readme.IsExcluded);
        Assert.False(readme.HasSecretFinding);
        Assert.True(secretFile.IsExcluded);
        Assert.True(secretFile.HasSecretFinding);
        Assert.True(secretFile.IsRecommendedExclusion);
        Assert.Contains("ApiKey", secretFile.SecretSummary, StringComparison.Ordinal);

        Assert.Equal(AdaptationPreSendPreview.UnknownQuotaValue, viewModel.CostQuotaEstimateDisplay);
        Assert.Equal("PROMPT", viewModel.PromptPreview);
        Assert.Equal(OpenCodeRouteId, harness.Adaptation.LastPreviewRouteId);
        Assert.Empty(harness.Adaptation.LastPreviewExcludedFiles);
    }

    [Fact]
    public async Task OpenForVersion_UsesCapabilitiesOfEachNativeModelOnTheAccount()
    {
        var harness = new AdaptationHarness();
        var model = CreateCatalogModel("model-1", "Account", "prov-1", BackendType.OpenCode,
            NativeModelId, 128000, IsRoutable: true) with
        {
            BackendModelIds = [NativeModelId, "opencode/second-model"],
            BackendCapabilities = new Dictionary<string, SanitizedModelCapabilities>
            {
                [NativeModelId] = new(ModelCapabilityFlags.Chat | ModelCapabilityFlags.ReasoningVariants, ["max"], [], 64000),
                ["opencode/second-model"] = new(ModelCapabilityFlags.Chat, [], [], 4096)
            }
        };
        harness.Catalog.PreviewCatalog = new SanitizedCapabilityCatalog([], [model], Now);
        var viewModel = harness.CreateViewModel();
        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        Assert.Equal(2, viewModel.AvailableRoutes.Count);
        var first = viewModel.AvailableRoutes.Single(route => route.Id == OpenCodeRouteId);
        var second = viewModel.AvailableRoutes.Single(route => route.Id != OpenCodeRouteId);
        Assert.Equal(new[] { "max" }, first.SupportedReasoningEfforts);
        Assert.Equal(64000, first.ContextWindow);
        Assert.True(first.SupportsReasoning);
        Assert.Empty(second.SupportedReasoningEfforts);
        Assert.Equal(4096, second.ContextWindow);
        Assert.False(second.SupportsReasoning);
    }

    [Fact]
    public async Task OpenForVersion_OmitsRoutesWhoseBackendCannotRunAdaptation()
    {
        var harness = new AdaptationHarness();
        harness.Catalog.PreviewCatalog = new SanitizedCapabilityCatalog(
            Array.Empty<SanitizedProviderInfo>(),
            new[]
            {
                CreateCatalogModel("model-1", "Primary Model", "prov-1", BackendType.OpenCode, NativeModelId, 128000, IsRoutable: true),
                CreateCatalogModel("acct-cursor", "Cursor", "prov-cursor", BackendType.CursorAcp, "cursor-model", null, IsRoutable: true),
                CreateCatalogModel("acct-mirasim", "Mirasim", "prov-mirasim", BackendType.Mirasim, "mirasim-model", null, IsRoutable: true),
                CreateCatalogModel("acct-starcli", "StarCli", "prov-starcli", BackendType.StarCliProxy, "starcli-model", null, IsRoutable: true),
                CreateCatalogModel("acct-agy", "Agy", "prov-agy", BackendType.Agy, "agy-model", null, IsRoutable: true)
            },
            Now);

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        var route = Assert.Single(viewModel.AvailableRoutes);

        Assert.Equal(OpenCodeRouteId, route.Id);
        Assert.DoesNotContain(viewModel.AvailableRoutes, item => item.Id.Contains("cursor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(viewModel.AvailableRoutes, item => item.Id.Contains("mirasim", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(viewModel.AvailableRoutes, item => item.Id.Contains("starcli", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenForVersion_OmitsRoutableOpenCodeRouteWithoutBackendModelId()
    {
        var harness = new AdaptationHarness();
        harness.Catalog.PreviewCatalog = new SanitizedCapabilityCatalog(
            Array.Empty<SanitizedProviderInfo>(),
            new[]
            {
                CreateCatalogModel("acct-oc", "Plugin Account", "prov-oc", BackendType.OpenCode, backendModelId: null, null, IsRoutable: true)
            },
            Now);

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        // OpenCode plugin accounts carry no native model id, so no route is offered: the account id is
        // never promoted to a model id just to keep the row selectable.
        Assert.Empty(viewModel.AvailableRoutes);
        Assert.Null(viewModel.SelectedRoute);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.CanStartAdaptation);
    }

    [Fact]
    public async Task SecretScannerHit_StaysExcludedAfterTheLabeledGestureAndIsNeverSentToTheModel()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        var readme = viewModel.PreSendFiles.Single(file => file.RelativePath == "README.md");
        var secretFile = viewModel.PreSendFiles.Single(file => file.RelativePath == "config/secrets.env");

        // The label says the box means "sent to the model"; the scanner hit starts unchecked and locked.
        Assert.False(secretFile.IsIncluded);
        Assert.True(secretFile.IsExcluded);
        Assert.False(secretFile.CanChangeExclusion);
        Assert.Contains("исключа", secretFile.ExclusionNote, StringComparison.OrdinalIgnoreCase);

        // Checking the box is the labeled "include" gesture: it must not include the secret file.
        secretFile.IsIncluded = true;

        Assert.False(secretFile.IsIncluded);
        Assert.True(secretFile.IsExcluded);

        // A direct attempt through the exclusion flag is refused too.
        secretFile.IsExcluded = false;

        Assert.True(secretFile.IsExcluded);

        // A file without findings follows the label both ways.
        readme.IsIncluded = false;
        Assert.True(readme.IsExcluded);

        readme.IsIncluded = true;
        Assert.True(readme.IsIncluded);

        readme.IsIncluded = false;

        Assert.False(viewModel.CanStartAdaptation);
        await viewModel.RefreshPreSendPreviewAsync();
        Assert.True(viewModel.CanStartAdaptation);
        await viewModel.StartAdaptationAsync();

        var request = Assert.Single(harness.Adaptation.StartRequests);
        Assert.Contains("config/secrets.env", request.UserExcludedFiles);
        Assert.Contains("README.md", request.UserExcludedFiles);
    }

    [Fact]
    public async Task OpenForVersion_WithoutRoutableRoutes_ReportsAnError()
    {
        var harness = new AdaptationHarness();
        harness.Catalog.PreviewCatalog = CreateCatalog(includeRoutableModel: false);

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        Assert.Empty(viewModel.AvailableRoutes);
        Assert.Null(viewModel.SelectedRoute);
        Assert.True(viewModel.HasError);
        Assert.Contains("No routable model", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanStartAdaptation);
    }

    [Fact]
    public async Task StartAdaptation_PassesSelectionAndExclusionsToTheService()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        viewModel.PreSendFiles.Single(file => file.RelativePath == "README.md").IsExcluded = true;
        viewModel.SelectedGoal = AdaptationGoal.Speed;
        viewModel.AllowExpandedSemanticScope = true;

        Assert.False(viewModel.CanStartAdaptation);
        await viewModel.RefreshPreSendPreviewAsync();
        Assert.True(viewModel.CanStartAdaptation);
        await viewModel.StartAdaptationAsync();

        var request = Assert.Single(harness.Adaptation.StartRequests);
        Assert.Equal("ver-1", request.WorkflowVersionId);
        Assert.Equal(OpenCodeRouteId, request.AdapterRouteId);
        Assert.NotEqual("model-1", request.AdapterRouteId);
        Assert.Equal(AdaptationGoal.Speed, request.Goal);
        Assert.True(request.AllowExpandedSemanticScope);
        Assert.Equal(new[] { "README.md", "config/secrets.env" }, request.UserExcludedFiles);
    }

    /// <summary>
    /// The pre-send preview is built from the same scope the user selected, so the prompt the user reviews
    /// before confirming cannot claim a scope the user did not choose.
    /// </summary>
    [Fact]
    public async Task PreparePreSendPreview_PassesTheSelectedExpandedScopeToTheService()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        Assert.False(harness.Adaptation.LastPreviewAllowExpandedSemanticScope);

        viewModel.AllowExpandedSemanticScope = true;

        await viewModel.RefreshPreSendPreviewAsync();

        Assert.True(harness.Adaptation.LastPreviewAllowExpandedSemanticScope);
    }

    [Fact]
    public async Task StartAdaptation_PresentsDiffMappingsAndSummary()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        Assert.True(viewModel.IsSessionActive);
        Assert.Equal("session-1", viewModel.ActiveSessionId);

        var diff = Assert.Single(viewModel.DiffFiles);
        Assert.Equal("prompts/executor.md", diff.RelativePath);
        Assert.Equal("Изменён", diff.KindDisplay);
        Assert.Equal("+1 -1", diff.ChangeBadge);
        Assert.Contains("@@", diff.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Same(diff, viewModel.SelectedDiffFile);
        Assert.Contains("@@", viewModel.SelectedDiffContent, StringComparison.Ordinal);
        Assert.Contains("1 modified", viewModel.DiffSummary, StringComparison.Ordinal);

        var mapping = Assert.Single(viewModel.RoleMappings);
        Assert.Equal("Executor", mapping.Role);
        Assert.Equal("model-1", mapping.TargetModelId);
        Assert.False(mapping.IsSemanticChange);

        Assert.False(viewModel.HasBlockers);
        Assert.Equal("No blockers", viewModel.BlockersSummary);
        Assert.True(viewModel.CanSaveCandidate);
        Assert.True(viewModel.CanAcceptAndActivate);
    }

    [Fact]
    public async Task StartAdaptation_WithBlockers_SurfacesIssuesAndBlockerSummary()
    {
        var harness = new AdaptationHarness();
        harness.Adaptation.StartResult = CreateCandidateResult(
            "session-1",
            mappings: new[] { CreateMapping() },
            blockers: new[] { AdaptationBlockerKind.MissingModel },
            issues: new[]
            {
                new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingModel,
                    "Executor",
                    "Model 'model-missing' not found in active catalog.")
            });

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        Assert.True(viewModel.HasBlockers);
        Assert.Equal("MissingModel", viewModel.BlockersSummary);
        Assert.True(viewModel.HasIssues);
        var issue = Assert.Single(viewModel.Issues);
        Assert.Equal("MissingModel", issue.KindDisplay);
        Assert.Equal("Executor", issue.RoleDisplay);
        Assert.Contains("model-missing", issue.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitFollowUp_WithoutPrompt_IsRefusedLocally()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        viewModel.FollowUpPrompt = "   ";

        await viewModel.SubmitFollowUpAsync();

        Assert.Empty(harness.Adaptation.FollowUpRequests);
        Assert.True(viewModel.HasError);
        Assert.Contains("уточняющий запрос", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitFollowUp_UpdatesCandidateAndClearsThePrompt()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        harness.Adaptation.FollowUpResult = CreateCandidateResult(
            "session-1",
            mappings: new[] { CreateMapping() },
            fileDiff: CreateFileDiff("README.md"));

        viewModel.FollowUpPrompt = "Please also update the README.";

        await viewModel.SubmitFollowUpAsync();

        var request = Assert.Single(harness.Adaptation.FollowUpRequests);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal("Please also update the README.", request.Prompt);
        Assert.Equal(string.Empty, viewModel.FollowUpPrompt);
        Assert.Equal("README.md", Assert.Single(viewModel.DiffFiles).RelativePath);
        Assert.Contains("Уточняющий шаг применён", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveCandidate_LeavesBindingUntouchedAndNotifiesTheLibrary()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();
        var libraryNotifications = 0;

        viewModel.OnLibraryChangedAsync = () =>
        {
            libraryNotifications++;
            return Task.CompletedTask;
        };

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.SaveCandidateAsync();

        Assert.Equal(new[] { "session-1" }, harness.Adaptation.SavedSessions);
        Assert.Empty(harness.Activation.ActivationRequests);
        Assert.Equal(1, libraryNotifications);
        Assert.Contains("Активная привязка осталась неизменной", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("версии 2", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanSaveCandidate);
    }

    [Fact]
    public async Task AcceptAndActivate_WithUnacknowledgedBlockers_IsRefusedBeforeActivation()
    {
        var harness = new AdaptationHarness();
        harness.Adaptation.StartResult = CreateCandidateResult(
            "session-1",
            blockers: new[] { AdaptationBlockerKind.DetectedSecret },
            issues: new[]
            {
                new AdaptationValidationIssue(
                    AdaptationBlockerKind.DetectedSecret,
                    null,
                    "Secret 'ApiKey' detected at README.md:1.")
            });

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        await viewModel.AcceptAndActivateAsync();

        Assert.Empty(harness.Adaptation.SavedSessions);
        Assert.Empty(harness.Activation.ActivationRequests);
        Assert.Equal(WorkflowAdaptationViewModel.AcknowledgeBlockersRequiredMessage, viewModel.ErrorMessage);
        Assert.True(viewModel.IsVisible);
    }

    /// <summary>
    /// An issue that reports content the bounded analysis could not read is not a difference the operator is
    /// being asked to accept, so the dialog never offers a decision for it and the activation stays refused.
    /// </summary>
    [Fact]
    public async Task AcceptAndActivate_WithUnverifiableSemanticBlocker_CannotBeAcknowledged()
    {
        var harness = new AdaptationHarness();
        harness.Adaptation.StartResult = CreateCandidateResult(
            "session-1",
            blockers: new[] { AdaptationBlockerKind.DisallowedSemanticChange },
            issues: new[]
            {
                new AdaptationValidationIssue(
                    AdaptationBlockerKind.DisallowedSemanticChange,
                    null,
                    "Semantic change detected in the candidate package (UnverifiableContent, stages/build.md): "
                    + "The semantic-bearing file 'stages/build.md' cannot be compared confidently.",
                    isNotClearable: true)
            });

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        var blocker = Assert.Single(viewModel.ActivationBlockers);

        Assert.False(blocker.CanBeAcknowledged);
        Assert.True(viewModel.HasUnverifiableSemanticBlockers);
        Assert.False(viewModel.CanAcceptAndActivate);

        blocker.IsAcknowledged = true;

        Assert.False(blocker.IsAcknowledged);
        Assert.Empty(viewModel.ConfirmedBlockerIssues);

        await viewModel.AcceptAndActivateAsync();

        Assert.Empty(harness.Activation.ActivationRequests);
        Assert.Equal(WorkflowAdaptationViewModel.UnverifiableSemanticScopeMessage, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task AcceptAndActivate_SavesTheCandidateFirstAndThenActivates()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();
        var libraryNotifications = 0;

        viewModel.OnLibraryChangedAsync = () =>
        {
            libraryNotifications++;
            return Task.CompletedTask;
        };

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        await viewModel.AcceptAndActivateAsync();

        Assert.Equal(new[] { "session-1" }, harness.Adaptation.SavedSessions);

        var request = Assert.Single(harness.Activation.ActivationRequests);
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal("pkg-1", request.WorkflowPackageId);
        Assert.Equal("ver-candidate", request.WorkflowVersionId);
        Assert.False(request.AcknowledgeBlockers);

        Assert.Equal(1, libraryNotifications);
        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.IsSessionActive);
    }

    [Fact]
    public async Task AcceptAndActivate_ForwardsOneDecisionPerShownIssue()
    {
        var harness = new AdaptationHarness();
        harness.Adaptation.StartResult = CreateCandidateResult(
            "session-1",
            blockers: new[] { AdaptationBlockerKind.MissingCapability, AdaptationBlockerKind.MissingModel },
            issues: new[]
            {
                new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingCapability,
                    "Executor",
                    "Model 'model-1' is marked not routable in the current catalog."),
                new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingModel,
                    "Reviewer",
                    "Model 'model-missing' not found in the current catalog.")
            });

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        Assert.Equal(2, viewModel.ActivationBlockers.Count);
        Assert.DoesNotContain(viewModel.ActivationBlockers, blocker => blocker.IsAcknowledged);
        Assert.False(viewModel.CanAcceptAndActivate);

        viewModel.ActivationBlockers[0].IsAcknowledged = true;
        Assert.False(viewModel.CanAcceptAndActivate);

        viewModel.ActivationBlockers[1].IsAcknowledged = true;
        Assert.True(viewModel.CanAcceptAndActivate);

        await viewModel.AcceptAndActivateAsync();

        var request = Assert.Single(harness.Activation.ActivationRequests);

        // The blanket flag is never used; the decisions travel as the exact issues they belong to.
        Assert.False(request.AcknowledgeBlockers);
        Assert.Empty(request.AcknowledgedBlockerKinds);
        Assert.Equal(
            new[] { AdaptationBlockerKind.MissingCapability, AdaptationBlockerKind.MissingModel },
            request.AcknowledgedBlockerIssues.Select(issue => issue.Kind).ToArray());
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public async Task AcceptAndActivate_WhenRevalidationReportsAnotherIssue_ClearsTheRecordedDecisions()
    {
        var harness = new AdaptationHarness();
        harness.Activation.Validation = new WorkflowActivationValidationResult(new[]
        {
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingModel,
                "Executor",
                "Model 'model-a' not found in the current catalog.")
        });
        harness.Activation.ShouldSucceed = false;

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        // The first attempt is refused and shows the freshly reported issue.
        await viewModel.AcceptAndActivateAsync();

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasError);
        Assert.Equal(
            "Model 'model-a' not found in the current catalog.",
            Assert.Single(viewModel.ActivationBlockers).Message);
        Assert.False(viewModel.CanAcceptAndActivate);

        viewModel.ActivationBlockers[0].IsAcknowledged = true;

        Assert.True(viewModel.CanAcceptAndActivate);

        // The same kind for a different model invalidates the recorded decision.
        harness.Activation.Validation = new WorkflowActivationValidationResult(new[]
        {
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingModel,
                "Executor",
                "Model 'model-b' not found in the current catalog.")
        });

        await viewModel.AcceptAndActivateAsync();

        var row = Assert.Single(viewModel.ActivationBlockers);

        Assert.False(row.IsAcknowledged);
        Assert.Contains("model-b", row.Message, StringComparison.Ordinal);
        Assert.False(viewModel.CanAcceptAndActivate);
        Assert.True(viewModel.IsVisible);

        var lastRequest = harness.Activation.ActivationRequests[^1];

        Assert.Contains("model-a", Assert.Single(lastRequest.AcknowledgedBlockerIssues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptAndActivate_ReusesAnAlreadySavedCandidate()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.SaveCandidateAsync();

        await viewModel.AcceptAndActivateAsync();

        Assert.Single(harness.Adaptation.SavedSessions);
        var request = Assert.Single(harness.Activation.ActivationRequests);
        Assert.Equal("ver-candidate", request.WorkflowVersionId);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public async Task AcceptAndActivate_WhenTheActivationIsBlocked_KeepsTheDialogOpen()
    {
        var harness = new AdaptationHarness();
        harness.Activation.Validation = new WorkflowActivationValidationResult(new[]
        {
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingModel,
                "Executor",
                "Model 'model-1' not found in the current catalog.")
        });
        harness.Activation.ShouldSucceed = false;

        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.AcceptAndActivateAsync();

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasBlockers);
        Assert.True(viewModel.HasError);
        Assert.Contains("MissingModel", viewModel.BlockersSummary, StringComparison.Ordinal);
        Assert.Contains(
            viewModel.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingModel);
    }

    [Fact]
    public async Task Discard_CleansTheSessionAndClosesTheDialog()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.DiscardAsync();

        Assert.Equal(new[] { "session-1" }, harness.Adaptation.DiscardedSessions);
        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.IsSessionActive);
        Assert.Null(viewModel.ActiveSessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedCandidateWithPendingCleanupKeepsIdentityUntilCloseOrActivationCanClean(bool activate)
    {
        var harness = new AdaptationHarness();
        harness.Adaptation.CleanupPending = true;
        harness.Adaptation.FailCleanup = true;
        var viewModel = harness.CreateViewModel();
        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.SaveCandidateAsync();
        Assert.Empty(viewModel.ErrorMessage);
        Assert.Contains("Версия сохранена", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("временные файлы пока не удалены", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanSaveCandidate);
        Assert.False(viewModel.CanSubmitFollowUp);
        Assert.False(viewModel.CanStartAdaptation);
        await viewModel.StartAdaptationAsync();
        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        Assert.Single(harness.Adaptation.StartRequests);
        Assert.Equal("session-1", viewModel.ActiveSessionId);
        if (activate) await viewModel.AcceptAndActivateAsync();
        else await viewModel.DiscardAsync();
        Assert.Single(harness.Adaptation.SavedSessions);
        Assert.Single(harness.Adaptation.DiscardedSessions);
        Assert.Empty(harness.Activation.ActivationRequests);
        Assert.True(viewModel.IsVisible);
        Assert.Equal("session-1", viewModel.ActiveSessionId);
        Assert.NotEmpty(viewModel.ErrorMessage);

        harness.Adaptation.FailCleanup = false;
        if (activate) await viewModel.AcceptAndActivateAsync();
        else await viewModel.DiscardAsync();
        Assert.Single(harness.Adaptation.SavedSessions);
        Assert.Equal(2, harness.Adaptation.DiscardedSessions.Count);
        Assert.Equal(activate ? 1 : 0, harness.Activation.ActivationRequests.Count);
        Assert.False(viewModel.IsVisible);
        Assert.Null(viewModel.ActiveSessionId);
    }

    [Fact]
    public async Task Cancel_ActiveUnsavedSession_DiscardsScratchBeforeForgettingIdentity()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();
        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        viewModel.CancelCommand.Execute(null);
        Assert.Equal(new[] { "session-1" }, harness.Adaptation.DiscardedSessions);
        Assert.False(viewModel.IsVisible);
        Assert.Null(viewModel.ActiveSessionId);
    }

    [Fact]
    public async Task Cancel_BeforeTheSessionStarts_TouchesNoService()
    {
        var harness = new AdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.IsVisible);
        Assert.Empty(harness.Adaptation.StartRequests);
        Assert.Empty(harness.Adaptation.DiscardedSessions);
    }

    private static AdaptationCandidateResult CreateCandidateResult(
        string sessionId,
        IReadOnlyList<SemanticRoleMapping>? mappings = null,
        IReadOnlyList<AdaptationBlockerKind>? blockers = null,
        IReadOnlyList<AdaptationValidationIssue>? issues = null,
        WorkflowFileDiff? fileDiff = null,
        string rationale = "Adaptation plan.")
    {
        var diffs = fileDiff is null
            ? new[] { CreateFileDiff("prompts/executor.md") }
            : new[] { fileDiff };

        return new AdaptationCandidateResult(
            sessionId,
            "ver-1",
            1,
            "model-1",
            "model-1",
            AdaptationGoal.Balanced,
            allowExpandedSemanticScope: false,
            @"C:\scratch\adaptation\session-1",
            mappings ?? Array.Empty<SemanticRoleMapping>(),
            rationale,
            Array.Empty<string>(),
            blockers ?? Array.Empty<AdaptationBlockerKind>(),
            issues ?? Array.Empty<AdaptationValidationIssue>(),
            WorkflowSecretScanReport.Empty,
            AdaptationReferenceValidationResult.Empty,
            new SemanticDiffResult(Array.Empty<AdaptationValidationIssue>(), Array.Empty<string>(), false),
            new WorkflowPackageDiff(diffs),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new[] { "README.md", "prompts/executor.md" },
            parseError: null);
    }

    private static WorkflowFileDiff CreateFileDiff(string relativePath)
    {
        return new WorkflowFileDiff(
            relativePath,
            WorkflowFileDiffKind.Modified,
            1,
            1,
            "--- a/" + relativePath + "\n+++ b/" + relativePath + "\n@@ -1 +1 @@\n-old\n+new\n",
            isBinary: false);
    }

    private static SemanticRoleMapping CreateMapping()
    {
        return new SemanticRoleMapping(
            WorkflowRole.Executor,
            "model-1",
            "model-1:model-1",
            "model-1",
            "Rebinding an existing role.",
            isSemanticChange: false);
    }

    private static SanitizedCapabilityCatalog CreateCatalog(bool includeRoutableModel = true)
    {
        var models = new List<SanitizedModelInfo>();

        if (includeRoutableModel)
        {
            models.Add(CreateCatalogModel(
                "model-1",
                "Primary Model",
                "prov-1",
                BackendType.OpenCode,
                NativeModelId,
                ContextWindow: 128000,
                IsRoutable: true));
        }

        models.Add(CreateCatalogModel(
            "model-disabled",
            "Disabled Model",
            "prov-1",
            BackendType.OpenCode,
            NativeModelId,
            ContextWindow: null,
            IsRoutable: false));

        return new SanitizedCapabilityCatalog(
            Array.Empty<SanitizedProviderInfo>(),
            models,
            Now);
    }

    private static SanitizedModelInfo CreateCatalogModel(
        string accountId,
        string displayName,
        string providerProfileId,
        BackendType backend,
        string? backendModelId,
        int? ContextWindow,
        bool IsRoutable)
    {
        return new SanitizedModelInfo(
            accountId,
            displayName,
            ModelCapabilityFlags.Chat,
            Array.Empty<string>(),
            new[] { "balanced" },
            ContextWindow,
            HealthState.Healthy,
            IsRoutable)
        {
            AccountId = accountId,
            ProviderProfileId = providerProfileId,
            Backend = backend,
            BackendModelId = backendModelId
        };
    }

    private sealed class AdaptationHarness
    {
        public AdaptationStubAdaptationService Adaptation { get; } = new();

        public AdaptationStubActivationService Activation { get; } = new();

        public AdaptationStubCatalogProvider Catalog { get; } = new();

        public WorkflowVersionItemViewModel Version { get; } = new(new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Now,
            null));

        public WorkflowPackageItemViewModel Package { get; } = new(new WorkflowPackage(
            "pkg-1",
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release" },
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            Now,
            Now));

        public Project Project { get; } = new(
            "project-1",
            "Project project-1",
            @"C:\work\project-1",
            null,
            isDirty: false,
            hasRequiredInstructions: true,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource);

        public AdaptationHarness()
        {
            Adaptation.Preview = new AdaptationPreSendPreview(
                "ver-1",
                1,
                HashA,
                "model-1",
                "model-1",
                AdaptationGoal.Balanced,
                new WorkflowSecretScanReport(
                    hasFindings: true,
                    new[] { new WorkflowSecretFinding("config/secrets.env", 1, "ApiKey", "***") },
                    new[] { "config/secrets.env" }),
                new[] { "README.md" },
                new[] { "config/secrets.env" },
                CreateCatalog(),
                AdaptationPreSendPreview.UnknownQuotaValue,
                AdaptationPreSendPreview.UnknownQuotaValue,
                reserveThreshold: null,
                promptPreview: "PROMPT");
        }

        public WorkflowAdaptationViewModel CreateViewModel()
        {
            return new WorkflowAdaptationViewModel(Adaptation, Activation, Catalog);
        }
    }

    private sealed class AdaptationStubCatalogProvider : ISanitizedCatalogProvider
    {
        public SanitizedCapabilityCatalog PreviewCatalog { get; set; } = CreateCatalog();

        public Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PreviewCatalog);
        }
    }

    private sealed class AdaptationStubAdaptationService : IWorkflowAdaptationService
    {
        public bool CleanupPending { get; set; }
        public bool FailCleanup { get; set; }
        public AdaptationPreSendPreview? Preview { get; set; }

        public Exception? InitialFailure { get; set; }

        public AdaptationCandidateResult? StartResult { get; set; }

        public AdaptationCandidateResult? FollowUpResult { get; set; }

        public string? LastPreviewRouteId { get; private set; }

        public IReadOnlyList<string> LastPreviewExcludedFiles { get; private set; } = Array.Empty<string>();

        public bool LastPreviewAllowExpandedSemanticScope { get; private set; }

        public List<AdaptationExecutionRequest> StartRequests { get; } = new();

        public List<AdaptationFollowUpRequest> FollowUpRequests { get; } = new();

        public List<string> SavedSessions { get; } = new();

        public Func<string, CancellationToken, Task<SaveCandidateVersionResult>>? SaveHandler { get; set; }

        public List<string> DiscardedSessions { get; } = new();

        public Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
            string workflowVersionId,
            string adapterRouteId,
            AdaptationGoal goal,
            IReadOnlyList<string>? userExcludedFiles = null,
            bool allowExpandedSemanticScope = false,
            CancellationToken cancellationToken = default)
        {
            if (InitialFailure is not null) return Task.FromException<AdaptationPreSendPreview>(InitialFailure);
            LastPreviewRouteId = adapterRouteId;
            LastPreviewExcludedFiles = userExcludedFiles ?? Array.Empty<string>();
            LastPreviewAllowExpandedSemanticScope = allowExpandedSemanticScope;

            return Task.FromResult(
                Preview ?? throw new InvalidOperationException("No pre-send preview configured."));
        }

        public Task<AdaptationCandidateResult> StartAdaptationAsync(
            AdaptationExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (InitialFailure is not null) return Task.FromException<AdaptationCandidateResult>(InitialFailure);
            StartRequests.Add(request);

            return Task.FromResult(
                StartResult ?? CreateCandidateResult("session-1", mappings: new[] { CreateMapping() }));
        }

        public Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
            AdaptationFollowUpRequest request,
            CancellationToken cancellationToken = default)
        {
            FollowUpRequests.Add(request);

            return Task.FromResult(
                FollowUpResult ?? StartResult
                ?? CreateCandidateResult("session-1", mappings: new[] { CreateMapping() }));
        }

        public Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            SavedSessions.Add(sessionId);

            if (SaveHandler is not null) return SaveHandler(sessionId, cancellationToken);

            return Task.FromResult(new SaveCandidateVersionResult(
                "ver-candidate",
                "pkg-1",
                2,
                HashA,
                WorkflowSourceType.SyntheticDraft,
                Now,
                activatedAtUtc: null,
                Array.Empty<AdaptationBlockerKind>()) { CleanupPending = CleanupPending });
        }

        public Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            DiscardedSessions.Add(sessionId);
            if (FailCleanup) throw new IOException("synthetic scratch cleanup failure");

            return Task.CompletedTask;
        }

        public AdaptationSessionSnapshot GetSessionSnapshot(string sessionId)
        {
            throw new NotSupportedException("The dialog does not read session snapshots directly.");
        }
    }

    private sealed class AdaptationStubActivationService : IWorkflowActivationService
    {
        public WorkflowActivationValidationResult Validation { get; set; } =
            WorkflowActivationValidationResult.Valid;

        public bool ShouldSucceed { get; set; } = true;

        public List<WorkflowActivationRequest> ActivationRequests { get; } = new();

        public List<(string ProjectId, string WorkflowPackageId, string TargetVersionId)> RollbackRequests { get; } = new();

        public Task<WorkflowActivationValidationResult> ValidateForActivationAsync(
            string workflowVersionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Validation);
        }

        public Task<WorkflowActivationResult> ActivateVersionAsync(
            WorkflowActivationRequest request,
            CancellationToken cancellationToken = default)
        {
            ActivationRequests.Add(request);

            if (!ShouldSucceed)
            {
                var blocked = Validation.HasBlockers
                    ? WorkflowActivationResult.Blocked(Validation, "Activation blocked by the engine.")
                    : WorkflowActivationResult.Failed("Activation failed.");

                return Task.FromResult(blocked);
            }

            var binding = new WorkflowBinding(
                Guid.NewGuid().ToString("N"),
                request.ProjectId,
                request.WorkflowPackageId,
                request.WorkflowVersionId,
                request.RoutePolicyId,
                Now,
                Now);

            return Task.FromResult(WorkflowActivationResult.Success(binding, Validation));
        }

        public Task<WorkflowRollbackResult> RollbackToVersionAsync(
            string projectId,
            string workflowPackageId,
            string targetVersionId,
            bool acknowledgeBlockers = false,
            IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
            IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
            CancellationToken cancellationToken = default)
        {
            RollbackRequests.Add((projectId, workflowPackageId, targetVersionId));

            var binding = new WorkflowBinding(
                Guid.NewGuid().ToString("N"),
                projectId,
                workflowPackageId,
                targetVersionId,
                routePolicyId: null,
                Now,
                Now);

            return Task.FromResult(WorkflowRollbackResult.Success(binding, null, targetVersionId));
        }
    }

    [Fact]
    public async Task AcceptAndActivate_AgainstTheRealActivationEngine_RequiresOneDecisionPerFreshIssue()
    {
        using var harness = new RealGateAdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.SourceVersion, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();

        // The real engine re-validates the saved candidate and finds a secret plus an unknown model.
        await viewModel.AcceptAndActivateAsync();

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasError);
        Assert.Equal(2, viewModel.ActivationBlockers.Count);
        Assert.DoesNotContain(viewModel.ActivationBlockers, blocker => blocker.IsAcknowledged);
        Assert.False(viewModel.CanAcceptAndActivate);

        var kinds = viewModel.ActivationBlockers.Select(blocker => blocker.Kind).OrderBy(kind => kind);

        Assert.Equal(
            new[] { AdaptationBlockerKind.MissingModel, AdaptationBlockerKind.DetectedSecret },
            kinds.ToArray());

        // No binding was written, and the very first attempt carried no decision at all.
        Assert.Null(await harness.ActiveVersionIdAsync("project-1"));

        var firstAttempt = Assert.Single(harness.Activation.ActivationRequests);

        Assert.False(firstAttempt.AcknowledgeBlockers);
        Assert.Empty(firstAttempt.AcknowledgedBlockerKinds);
        Assert.Empty(firstAttempt.AcknowledgedBlockerIssues);

        viewModel.ActivationBlockers[0].IsAcknowledged = true;
        Assert.False(viewModel.CanAcceptAndActivate);

        viewModel.ActivationBlockers[1].IsAcknowledged = true;
        Assert.True(viewModel.CanAcceptAndActivate);

        var confirmedIdentities = viewModel.ActivationBlockers
            .Select(blocker => blocker.Issue.Identity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();

        await viewModel.AcceptAndActivateAsync();

        var secondAttempt = harness.Activation.ActivationRequests[^1];

        Assert.False(secondAttempt.AcknowledgeBlockers);
        Assert.Empty(secondAttempt.AcknowledgedBlockerKinds);
        Assert.Equal(
            confirmedIdentities,
            secondAttempt.AcknowledgedBlockerIssues
                .Select(issue => issue.Identity)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray());

        Assert.Equal("ver-candidate", await harness.ActiveVersionIdAsync("project-1"));
        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.HasActivationBlockers);
    }

    [Fact]
    public async Task AcceptAndActivate_AgainstTheRealActivationEngine_DropsDecisionsForAChangedIssue()
    {
        using var harness = new RealGateAdaptationHarness();
        var viewModel = harness.CreateViewModel();

        await viewModel.OpenForVersionAsync(harness.SourceVersion, harness.Package, harness.Project);
        await viewModel.StartAdaptationAsync();
        await viewModel.AcceptAndActivateAsync();

        foreach (var blocker in viewModel.ActivationBlockers)
        {
            blocker.IsAcknowledged = true;
        }

        Assert.True(viewModel.CanAcceptAndActivate);

        // The revalidation now reports the missing model of a different model for the same role.
        await harness.ReplaceCandidateModelAsync("model-another");

        await viewModel.AcceptAndActivateAsync();

        Assert.True(viewModel.IsVisible);
        Assert.Null(await harness.ActiveVersionIdAsync("project-1"));
        Assert.Equal(2, viewModel.ActivationBlockers.Count);
        Assert.DoesNotContain(viewModel.ActivationBlockers, blocker => blocker.IsAcknowledged);
        Assert.False(viewModel.CanAcceptAndActivate);
        Assert.Contains(
            viewModel.ActivationBlockers,
            blocker => blocker.Message.Contains("model-another", StringComparison.Ordinal));
    }

    /// <summary>
    /// Composes the real <see cref="WorkflowActivationService"/> over in-memory repositories, so the
    /// dialog is driven through the shipped fail-closed gate and not through a stub that would accept
    /// the legacy blanket flag.
    /// </summary>
    private sealed class RealGateAdaptationHarness : IDisposable
    {
        private readonly string _root;
        private readonly WorkflowBlobStore _blobStore;
        private readonly InMemoryWorkflowVersionRepository _versionRepository = new();
        private readonly InMemoryWorkflowBindingRepository _bindingRepository = new();
        private readonly InMemoryWorkflowPackageRepository _packageRepository = new();

        public RealGateAdaptationHarness()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "llmworkgui-activation-gate-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(_root);

            _blobStore = new WorkflowBlobStore(_root);

            var time = new FixedTimeProvider(Now);

            var activationService = new WorkflowActivationService(
                _versionRepository,
                _bindingRepository,
                new WorkflowBindingService(
                    _bindingRepository,
                    _packageRepository,
                    _versionRepository,
                    time),
                new LibraryStubCatalogProvider(),
                new WorkflowSecretScanner(new SensitiveDataFilter()),
                new ScratchWorkspaceManager(_blobStore, new SafeArchiveValidator()),
                new WorkflowManifestParser(),
                new SemanticDiffEngine(),
                time);

            Activation = new RecordingActivationService(activationService);
            CandidateStore = new CandidateStoringAdaptationService(_blobStore, _versionRepository);
            Adaptation = CandidateStore;
            Package = new WorkflowPackageItemViewModel(new WorkflowPackage(
                "pkg-1",
                "Release Workflow",
                "Coordinates release tasks",
                new[] { "release" },
                WorkflowSourceType.ZipArchive,
                "sha256:" + new string('c', 64),
                "sha256:" + new string('c', 64),
                Now,
                Now));

            _packageRepository.UpsertAsync(Package.Package).GetAwaiter().GetResult();

            SourceVersion = new WorkflowVersionItemViewModel(new WorkflowVersion(
                "ver-1",
                "pkg-1",
                1,
                "sha256:" + new string('d', 64),
                "sha256:" + new string('d', 64),
                WorkflowSourceType.ZipArchive,
                null,
                null,
                null,
                null,
                null,
                Now,
                null));
        }

        public RecordingActivationService Activation { get; }

        public IWorkflowAdaptationService Adaptation { get; }

        public CandidateStoringAdaptationService CandidateStore { get; }

        public WorkflowPackageItemViewModel Package { get; }

        public WorkflowVersionItemViewModel SourceVersion { get; }

        public Project Project { get; } = new(
            "project-1",
            "Project project-1",
            @"C:\work\project-1",
            null,
            isDirty: false,
            hasRequiredInstructions: true,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource);

        public WorkflowAdaptationViewModel CreateViewModel() =>
            new(Adaptation, Activation, new LibraryStubCatalogProvider());
        public async Task<string?> ActiveVersionIdAsync(string projectId)
        {
            var binding = await _bindingRepository.GetByProjectAndPackageAsync(projectId, "pkg-1");

            return binding?.ActiveVersionId;
        }

        /// <summary>
        /// Re-stores the saved candidate with different bindings, which is the drift the revalidation
        /// gate has to survive. The blob of the candidate stays byte-identical.
        /// </summary>
        public async Task ReplaceCandidateModelAsync(string modelId)
        {
            var candidate = await _versionRepository.GetByIdAsync(CandidateStore.CandidateVersionId);

            if (candidate is null)
            {
                throw new InvalidOperationException("The candidate was not stored yet.");
            }

            await _versionRepository.UpsertAsync(new WorkflowVersion(
                candidate.Id,
                candidate.WorkflowPackageId,
                candidate.VersionNumber,
                candidate.BlobId,
                candidate.OriginalHash,
                candidate.SourceType,
                candidate.EntrypointsJson,
                candidate.DeclaredRolesJson,
                CreateBindingsJson(modelId),
                candidate.CompatibilityReportJson,
                candidate.CreationMetadataJson,
                candidate.CreatedAtUtc,
                candidate.ActivatedAtUtc));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string CreateBindingsJson(string modelId) =>
            "[{\"role\":\"Executor\",\"originalRoute\":\"model-1\",\"targetRoute\":\"" + modelId
            + "\",\"targetModelId\":\"" + modelId + "\",\"rationale\":\"Test mapping.\","
            + "\"isSemanticChange\":false}]";

        /// <summary>
        /// Stores a real candidate version row with a real blob, so the real activation engine can
        /// re-validate it. The saved candidate carries a secret and an unknown model reference.
        /// </summary>
        public sealed class CandidateStoringAdaptationService : IWorkflowAdaptationService
        {
            private readonly WorkflowBlobStore _blobStore;
            private readonly InMemoryWorkflowVersionRepository _versionRepository;

            public CandidateStoringAdaptationService(
                WorkflowBlobStore blobStore,
                InMemoryWorkflowVersionRepository versionRepository)
            {
                _blobStore = blobStore;
                _versionRepository = versionRepository;
            }

            public string CandidateVersionId { get; private set; } = string.Empty;

            public Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
                string workflowVersionId,
                string adapterRouteId,
                AdaptationGoal goal,
                IReadOnlyList<string>? userExcludedFiles = null,
                bool allowExpandedSemanticScope = false,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new AdaptationPreSendPreview(
                    workflowVersionId,
                    1,
                    "sha256:" + new string('d', 64),
                    adapterRouteId,
                    adapterRouteId,
                    goal,
                    WorkflowSecretScanReport.Empty,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new SanitizedCapabilityCatalog(
                        Array.Empty<SanitizedProviderInfo>(),
                        Array.Empty<SanitizedModelInfo>(),
                        Now),
                    AdaptationPreSendPreview.UnknownQuotaValue,
                    AdaptationPreSendPreview.UnknownQuotaValue,
                    reserveThreshold: null,
                    promptPreview: "PROMPT"));

            public Task<AdaptationCandidateResult> StartAdaptationAsync(
                AdaptationExecutionRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateCandidateResult("session-1"));

            public Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
                AdaptationFollowUpRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateCandidateResult("session-1"));

            public async Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
                string sessionId,
                CancellationToken cancellationToken = default)
            {
                CandidateVersionId = "ver-candidate";

                var archiveBytes = CreateArchive(("README.md", "# Candidate api_key=sk-abcdefgh12345678"));
                var blob = await _blobStore.SaveBlobAsync(new MemoryStream(archiveBytes), cancellationToken);

                await _versionRepository.UpsertAsync(new WorkflowVersion(
                    CandidateVersionId,
                    "pkg-1",
                    2,
                    blob.BlobId,
                    blob.BlobId,
                    WorkflowSourceType.SyntheticDraft,
                    entrypointsJson: null,
                    declaredRolesJson: """["Executor"]""",
                    bindingsJson: CreateBindingsJson("model-missing"),
                    compatibilityReportJson: null,
                    creationMetadataJson: null,
                    Now,
                    activatedAtUtc: null),
                    cancellationToken);

                return new SaveCandidateVersionResult(
                    CandidateVersionId,
                    "pkg-1",
                    2,
                    blob.BlobId,
                    WorkflowSourceType.SyntheticDraft,
                    Now,
                    activatedAtUtc: null,
                    Array.Empty<AdaptationBlockerKind>());
            }

            public Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public AdaptationSessionSnapshot GetSessionSnapshot(string sessionId) =>
                throw new NotSupportedException("The dialog does not read session snapshots directly.");

            private static byte[] CreateArchive(params (string Path, string Content)[] entries)
            {
                using var buffer = new MemoryStream();

                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (path, content) in entries)
                    {
                        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);

                        using var stream = entry.Open();

                        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

                        stream.Write(bytes, 0, bytes.Length);
                    }
                }

                return buffer.ToArray();
            }
        }

        /// <summary>Forwards every call to the real engine and records what the dialog actually sent.</summary>
        public sealed class RecordingActivationService : IWorkflowActivationService
        {
            private readonly IWorkflowActivationService _inner;

            public RecordingActivationService(IWorkflowActivationService inner)
            {
                _inner = inner;
            }

            public List<WorkflowActivationRequest> ActivationRequests { get; } = new();

            public Task<WorkflowActivationValidationResult> ValidateForActivationAsync(
                string workflowVersionId,
                CancellationToken cancellationToken = default) =>
                _inner.ValidateForActivationAsync(workflowVersionId, cancellationToken);

            public Task<WorkflowActivationResult> ActivateVersionAsync(
                WorkflowActivationRequest request,
                CancellationToken cancellationToken = default)
            {
                ActivationRequests.Add(request);

                return _inner.ActivateVersionAsync(request, cancellationToken);
            }

            public Task<WorkflowRollbackResult> RollbackToVersionAsync(
                string projectId,
                string workflowPackageId,
                string targetVersionId,
                bool acknowledgeBlockers = false,
                IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
                IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
                CancellationToken cancellationToken = default) =>
                _inner.RollbackToVersionAsync(
                    projectId,
                    workflowPackageId,
                    targetVersionId,
                    acknowledgeBlockers,
                    acknowledgedBlockerKinds,
                    acknowledgedBlockerIssues,
                    cancellationToken);
        }
    }
}
