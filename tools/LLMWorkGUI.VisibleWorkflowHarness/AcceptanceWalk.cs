using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// The scripted visible acceptance walk. Every step acts on a control that is really on the shown window:
/// the button's own <see cref="ICommand"/> is executed, the shipped <c>Ctrl+8</c> <see cref="KeyBinding"/>
/// is executed with its own command and parameter, and the artifact bytes come from a real file on disk.
/// No view model method is called directly and no verdict, session or route is minted.
/// <para>
/// What the walk deliberately does not attempt is the duplicate-start refusal behind the now-unoffered Start
/// button: it is not reachable from the window, and forcing it would mean calling a view model method, which
/// this harness does not do. That refusal is covered by the product command test
/// <c>ADirectSecondStartIsStillRefusedByNameAndCreatesNothing</c>, which runs against the same real
/// composition.
/// </para>
/// </summary>
internal sealed class AcceptanceWalk
{
    private const string NotReported = "Not reported";

    private readonly Window _window;
    private readonly UnifiedWorkspaceShellViewModel _shell;
    private readonly WorkflowLibraryViewModel _library;
    private readonly IServiceProvider _services;
    private readonly HarnessOptions _options;
    private readonly AcceptanceReport _report;
    private readonly string _documentsDirectory;

    private string? _runId;
    private string? _stageId;

    public AcceptanceWalk(
        Window window,
        UnifiedWorkspaceShellViewModel shell,
        WorkflowLibraryViewModel library,
        IServiceProvider services,
        HarnessOptions options,
        AcceptanceReport report)
    {
        _window = window;
        _shell = shell;
        _library = library;
        _services = services;
        _options = options;
        _report = report;
        _documentsDirectory = Path.Combine(options.RunRoot, "documents");
    }

    public async Task RunAsync()
    {
        await WindowCapture.SettleAsync();

        await WindowVisibleAsync();
        await OnboardingDismissedAsync();
        await LibraryRefreshedAsync();
        await AssignmentRecordedAsync();
        await NavigatedToWorkflowsScreenAsync();
        await ConsoleOpenedAsync();
        await ConsoleStudioBeforeRunAsync();
        await StartFromConsoleAsync();
        await FirstStageMissingArtifactRefusalAsync();
        await AttachedTaskSpecificationDocumentAsync();
        await AdvancedAfterAttachAsync();
        await WalkedDocumentStagesAsync();
        await DocumentBundleAttachedAsync();
        await AssignedReviewRefusalAsync();
        await ReviewerGateRefusalAsync();
        UserApprovalStageBlocked();
        await ConsoleStudioPinnedGraphAsync();
        await MonitorSchemaAsync();
        await MonitorActivityAsync();
        await MonitorInspectorDrawerAsync();
        DeclaredRouteIsNotObservedEvidence();
        await LightThemeAsync();
        await DarkThemeRestoredAsync();
        await ReviewEvidenceRepositoryEmptyAsync();
    }

    private async Task WindowVisibleAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "window",
            "Shipped window is visible with normal chrome",
            "Show the shipped MainWindow with the unified workspace shell.",
            "Window is visible, activated, centred, has normal chrome and shows the unified shell."));

        try
        {
            await SettleAsync();

            var shellView = (FrameworkElement)_window.FindName("UnifiedWorkspaceShell");
            var baselinePanes = (FrameworkElement)_window.FindName("ShellPanes");

            var observed =
                $"IsVisible={_window.IsVisible}; ShowActivated={_window.ShowActivated}; "
                + $"StartupLocation={_window.WindowStartupLocation}; WindowStyle={_window.WindowStyle}; "
                + $"Title='{_window.Title}'; Shell={shellView.Visibility}; BaselinePanes={baselinePanes.Visibility}";

            var passed = _window.IsVisible
                && _window.ShowActivated
                && _window.WindowStartupLocation == WindowStartupLocation.CenterScreen
                && _window.WindowStyle == WindowStyle.SingleBorderWindow
                && shellView.Visibility == Visibility.Visible
                && baselinePanes.Visibility == Visibility.Collapsed;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "01-window-visible");

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                observed,
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task OnboardingDismissedAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "onboarding",
            "First-run onboarding is dismissed through its own control",
            "Press the shipped Dismiss button of the onboarding overlay.",
            "The overlay closes and the shell behind it becomes interactive."));

        try
        {
            await SettleAsync();

            if (!_shell.IsOnboardingOpen)
            {
                _report.Add(step.Complete(
                    AcceptanceOutcome.Pass,
                    "Onboarding was not open for this run.",
                    null,
                    "The auto-open only happens on a first run whose completion was never persisted."));

                return;
            }

            UiProbe.Invoke(UiProbe.Require<Button>(_window, "DismissOnboardingButton"));
            await SettleAsync();

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "02-shell-after-onboarding");

            _report.Add(step.Complete(
                _shell.IsOnboardingOpen ? AcceptanceOutcome.Fail : AcceptanceOutcome.Pass,
                $"IsOnboardingOpen={_shell.IsOnboardingOpen}",
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task LibraryRefreshedAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "refresh",
            "Workflows screen loads the seeded project and active binding",
            "Press the shipped Обновить (Refresh) button on the Workflows screen.",
            "The seeded project, package, version and the project's active binding are the selection."));

        try
        {
            await NavigateToWorkflowsAsync();
            var refresh = UiProbe.RequireButtonByCommand<Button>(
                _window,
                _library.RefreshCommand,
                "Workflows screen refresh button");

            UiProbe.Invoke(refresh);
            await WaitUntilAsync(() => !_library.IsBusy, "the library refresh finished");
            await SettleAsync();

            var observed =
                $"Project={_library.SelectedProject?.Id ?? "none"}; Package={_library.SelectedPackage?.Id ?? "none"}; "
                + $"Version={_library.SelectedVersion?.Id ?? "none"}; "
                + $"CanStart={_library.CanStartAssignedRun}; CanAdvance={_library.CanAdvanceObservedRun}";

            var passed = _library.SelectedProject?.Id == AcceptanceSeed.ProjectId
                && _library.SelectedPackage?.Id == AcceptanceSeed.PackageId
                && _library.SelectedVersion?.Id == AcceptanceSeed.VersionId
                && _library.CanStartAssignedRun
                && !_library.CanAdvanceObservedRun;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "03-workflows-screen");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task AssignmentRecordedAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "assignment",
            "The built-in standard template is assigned to the project",
            "Assign workflow-standard-development@1 through the production studio service before the run starts.",
            "A stored assignment exists for the project, template and version."));

        try
        {
            var assignment = await _services.GetRequiredService<IWorkflowTemplateStore>()
                .GetAssignmentAsync(AcceptanceSeed.ProjectId)
                .ConfigureAwait(true);

            var passed = assignment is not null
                && string.Equals(assignment.TemplateId, "workflow-standard-development", StringComparison.Ordinal)
                && assignment.TemplateVersion == 1;

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                assignment is null
                    ? "no stored assignment"
                    : $"{assignment.TemplateId}@{assignment.TemplateVersion} ({assignment.AssignmentId})"));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task NavigatedToWorkflowsScreenAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "navigation",
            "Ctrl+8 opens the Workflows screen",
            "Execute the shipped Ctrl+D8 KeyBinding of the unified shell.",
            "The shell reports the Workflows screen as the active screen."));

        try
        {
            // Move off the Workflows screen first so the shortcut has something to switch to.
            UiProbe.InvokeShortcut(_window, Key.D1, ModifierKeys.Control);
            await SettleAsync();
            var before = _shell.ActiveScreenId;

            UiProbe.InvokeShortcut(_window, Key.D8, ModifierKeys.Control);
            await SettleAsync();

            var passed = before == ScreenId.Workspace && _shell.ActiveScreenId == ScreenId.Workflows;
            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "04-ctrl8-workflows");

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                $"{before} -> {_shell.ActiveScreenId} (title '{_shell.ActiveScreenTitle}')",
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task ConsoleOpenedAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "console-open",
            "Workflow Console opens with the shipped run buttons",
            "Press the shipped 'Консоль процессов' button of the shell.",
            "The console overlay is open and its Start/Advance buttons are the shipped product buttons."));

        try
        {
            var open = UiProbe.Require<Button>(_window, "OpenWorkflowConsoleButton");
            UiProbe.Invoke(open);
            await SettleAsync();

            var start = UiProbe.Require<Button>(_window, "ConsoleStartRunButton");
            var advance = UiProbe.Require<Button>(_window, "ConsoleAdvanceRunButton");

            var passed = _shell.IsWorkflowConsoleOpen && start.IsEnabled && !advance.IsEnabled;
            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "05-console-open");

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                $"ConsoleOpen={_shell.IsWorkflowConsoleOpen}; StartEnabled={start.IsEnabled}; AdvanceEnabled={advance.IsEnabled}",
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task ConsoleStudioBeforeRunAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "console-studio-before",
            "Console Studio shows the editable template and no pinned run graph",
            "Read the Studio panels of the Workflow Console before a run exists.",
            "The editable template schema is shown while the pinned run graph is empty."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleTemplatesButton"));
            await SettleAsync();

            var studio = _shell.WorkflowConsole!.Studio!;
            var observed =
                $"Template={studio.TemplateGraphSummary}; RunGraph={studio.RunGraphSummary}; HasRunGraph={studio.HasRunGraph}";

            var passed = !studio.HasRunGraph
                && studio.TemplateGraphSummary.Contains(
                    WorkflowScheme.TaskSpecificationStageId,
                    StringComparison.Ordinal);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "06-console-studio-before-run");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task StartFromConsoleAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "start",
            "Start pins a real run to the assigned built-in template",
            "Press the shipped 'Запустить run' button of the Workflow Console.",
            "A persisted run is pinned to workflow-standard-development@1 on stage-task-specification, that "
            + "stage's TaskSpecificationDocument requirement is shown, Advance is offered, and a second Start "
            + "is unoffered with the run in the way named as the reason."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleStartRunButton"));
            await WaitUntilAsync(() => !_library.IsBusy && _library.HasObservedRun, "the start command finished");
            await SettleAsync();

            var run = _library.ObservedRun;
            _runId = run?.Id;
            _stageId = run?.CurrentStageId;
            _report.RunId = _runId;

            var stored = await CountRunsAsync();
            var requirement = _library.StageArtifactRequirementDisplay;
            var advance = UiProbe.Require<Button>(_window, "ConsoleAdvanceRunButton");

            // The observed run is not terminal, so the shipped Start action is taken away from the operator
            // instead of being offered and refused on click. What has to hold is that the button says why
            // and that the refusal reason names the run that is in the way.
            var secondStart = UiProbe.Require<Button>(_window, "ConsoleStartRunButton");
            var startStillOffered = secondStart.IsEnabled;
            var unavailableReason = _library.StartAssignedRunUnavailableReason;
            var visibleReason = ReadText(UiProbe.Require<FrameworkElement>(_window, "ConsoleStartRunReasonText"));

            var storedAfterSecondStart = await CountRunsAsync();

            var observed =
                $"Run={run?.Id}; Template={_library.ObservedRunTemplateDisplay}; Stage={run?.CurrentStageId}; "
                + $"State={run?.State}; StoredRuns={stored}; Requirement={requirement}; "
                + $"FailureRoute={_library.ObservedRunFailureRouteDisplay}; "
                + $"AdvanceEnabled={advance.IsEnabled}; StartStillOffered={startStillOffered}; "
                + $"StartUnavailableReason={unavailableReason}; VisibleStartReason={visibleReason}; "
                + $"StoredRunsAfterSecondStart={storedAfterSecondStart}";

            var passed = run is not null
                && run.TemplateId == "workflow-standard-development"
                && run.TemplateVersion == 1
                && run.CurrentStageId == WorkflowScheme.TaskSpecificationStageId
                && stored == 1
                && requirement.Contains("TaskSpecificationDocument", StringComparison.Ordinal)
                && advance.IsEnabled
                && !startStillOffered
                && unavailableReason.Contains(run!.Id, StringComparison.Ordinal)
                && visibleReason.Contains(run.Id, StringComparison.Ordinal)
                && storedAfterSecondStart == 1;

            _report.ObservedStageId = run?.CurrentStageId;
            _report.ObservedTemplateDisplay = _library.ObservedRunTemplateDisplay;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "07-console-run-started");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private static string ReadText(FrameworkElement element) =>
        element is TextBlock block ? block.Text : string.Empty;

    private async Task FirstStageMissingArtifactRefusalAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "missing-artifact",
            "First stage refuses the advance and displays the named document requirement",
            "Press the shipped 'Переход по схеме' button before any document is attached.",
            "A safe refusal is shown, the visible requirement names TaskSpecificationDocument, and the stored run stays on stage-task-specification."));

        try
        {
            var before = _library.ObservedRun?.CurrentStageId;

            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleAdvanceRunButton"));
            await WaitUntilAsync(() => !_library.IsBusy, "the advance command finished");
            await SettleAsync();

            var after = _library.ObservedRun?.CurrentStageId;
            var transitions = (await ReloadRunAsync())?.Transitions.Count ?? 0;

            var requirement = UiProbe.Descendants<TextBlock>(_window).First(block => block.IsVisible
                && System.Windows.Data.BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path?.Path
                    == "StageArtifactRequirementDisplay").Text;
            var observed = $"Blocker={_library.Blocker}; VisibleRequirement={requirement}; Stage {before} -> {after}; Transitions={transitions}";

            var passed = !string.IsNullOrWhiteSpace(_library.Blocker)
                && _library.Blocker.Contains("Переход стадии не выполнен", StringComparison.Ordinal)
                && requirement.Contains("TaskSpecificationDocument", StringComparison.Ordinal)
                && before == WorkflowScheme.TaskSpecificationStageId
                && after == WorkflowScheme.TaskSpecificationStageId
                && transitions == 0;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "08-missing-artifact-refusal");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task AttachedTaskSpecificationDocumentAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "attach-task-specification",
            "Real artifact bytes are attached through the Workflows screen",
            "Type a real local file path into the shipped path box and press 'Прикрепить артефакт'.",
            "The bytes of that file are stored as the stage artifact and the screen reports its hash."));

        try
        {
            await CloseConsoleAsync();

            var (path, expectedHash) = AcceptanceSeed.WriteStageDocument(
                _documentsDirectory,
                "task-specification.md",
                "TaskSpecificationDocument",
                WorkflowScheme.TaskSpecificationStageId);

            await AttachAsync(path);

            var stored = await CountArtifactsAsync();
            var blobOk = await BlobVerifiesAsync(expectedHash);
            var fileHash = await HashFileAsync(path);

            var observed =
                $"Artifacts={stored}; BlobVerifies={blobOk}; FileUnchanged={fileHash == expectedHash}; "
                + $"Panel={_library.StageArtifactRequirementDisplay}";

            var passed = stored == 1 && blobOk && fileHash == expectedHash
                && _library.StageArtifactRequirementDisplay.Contains(expectedHash, StringComparison.Ordinal);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "09-artifact-attached");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task AdvancedAfterAttachAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "advance-after-attach",
            "Advance succeeds once the real document is stored",
            "Press the shipped 'Переход по схеме' button of the Workflows screen.",
            "The run moves to stage-architecture and that stage's ArchitectureDocument requirement is shown."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "LibraryAdvanceRunButton"));
            await WaitUntilAsync(() => !_library.IsBusy, "the advance command finished");
            await SettleAsync();

            var stage = _library.ObservedRun?.CurrentStageId;
            _stageId = stage;

            var observed = $"Stage={stage}; Requirement={_library.StageArtifactRequirementDisplay}";

            var passed = stage == WorkflowScheme.ArchitectureStageId
                && _library.StageArtifactRequirementDisplay.Contains("ArchitectureDocument", StringComparison.Ordinal);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "10-stage-architecture");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task WalkedDocumentStagesAsync()
    {
        var stage = _report.Add(new AcceptanceStep(
            "walk-documents",
            "Later document stages are walked with real bytes only",
            "Attach and advance stage-architecture, stage-technical-specification and stage-roadmap in turn.",
            "The run reaches stage-document-review with a stored artifact on each stage it passed."));

        try
        {
            var expected = new (string StageId, string Kind)[]
            {
                (WorkflowScheme.ArchitectureStageId, "ArchitectureDocument"),
                (WorkflowScheme.TechnicalSpecificationStageId, "TechnicalSpecificationDocument"),
                (WorkflowScheme.RoadmapStageId, "RoadmapDocument")
            };

            var trail = new List<string>();

            foreach (var (stageId, kind) in expected)
            {
                var (path, _) = AcceptanceSeed.WriteStageDocument(
                    _documentsDirectory,
                    kind + ".md",
                    kind,
                    stageId);

                await AttachAsync(path);
                trail.Add($"{stageId}:{kind} attached");

                UiProbe.Invoke(UiProbe.Require<Button>(_window, "LibraryAdvanceRunButton"));
                await WaitUntilAsync(() => !_library.IsBusy, $"the advance out of {stageId} finished");
                await SettleAsync();

                trail.Add($"{stageId} -> {_library.ObservedRun?.CurrentStageId}");
            }

            _stageId = _library.ObservedRun?.CurrentStageId;

            var passed = _library.ObservedRun?.CurrentStageId == WorkflowScheme.DocumentReviewStageId;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "11-stage-document-review");

            _report.Add(stage.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                string.Join(" | ", trail),
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(stage.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task DocumentBundleAttachedAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "document-bundle",
            "Document review has a current DocumentBundle and two missing reviewer verdicts",
            "Attach a real DocumentBundle on stage-document-review.",
            "The reviewer gate reports Reviewer and Architect as missing on the hash of that bundle."));

        try
        {
            var (path, hash) = AcceptanceSeed.WriteStageDocument(
                _documentsDirectory,
                "DocumentBundle.md",
                "DocumentBundle",
                WorkflowScheme.DocumentReviewStageId);

            await AttachAsync(path);

            var gate = _library.ReviewGateStatus;
            var roles = string.Join(", ", gate.Roles.Select(role => $"{role.Role}={role.State}"));

            var observed =
                $"Stage={gate.StageId}; Kind={gate.RequiredArtifactKind}; Hash={gate.CurrentArtifactHash}; "
                + $"Verified={gate.ArtifactBytesVerified}; Roles=[{roles}]; ExpectedHash={hash}";

            var passed = gate.Availability == WorkflowReviewGateAvailability.Evaluated
                && gate.CurrentArtifactHash == hash
                && gate.ArtifactBytesVerified
                && gate.Roles.Count == 2
                && gate.Roles.All(role => role.State == WorkflowReviewGateRoleState.Missing);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "12-document-review-gate");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task AssignedReviewRefusalAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "assigned-review",
            "Assigned review click records its named channel refusal and nothing else",
            "Press the shipped 'Запросить назначенное ревью' button on stage-document-review.",
            "The named refusal of the declared route is shown and no reviewer evidence, session or execution "
            + "is persisted."));

        try
        {
            var before = await CountReviewEvidenceAsync();

            UiProbe.Invoke(UiProbe.Require<Button>(_window, "LibraryRequestAssignedReviewButton"));
            await WaitUntilAsync(() => !_library.IsBusy, "the review request finished");
            await SettleAsync();

            var after = await CountReviewEvidenceAsync();
            var sessions = await CountRunSessionsAsync();
            var executions = await CountRunExecutionsAsync();
            var verdicts = await CountRunVerdictsAsync();

            var detail = $"{_library.AssignedReviewStatusDisplay} :: {_library.AssignedReviewDetailDisplay}";
            var observed =
                $"Status={_library.AssignedReviewStatusDisplay}; EvidenceRows {before} -> {after}; "
                + $"Sessions={sessions}; Executions={executions}; Verdicts={verdicts}";

            var passed = before == 0
                && after == 0
                && sessions == 0
                && executions == 0
                && verdicts == 0
                && !string.IsNullOrWhiteSpace(_library.AssignedReviewStatusDisplay);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "13-assigned-review-refusal");

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                observed,
                evidence,
                detail));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task ReviewerGateRefusalAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "reviewer-gate",
            "Advance is refused by the reviewer gate with a current DocumentBundle",
            "Press the shipped 'Переход по схеме' button on stage-document-review.",
            "The refusal names the missing reviewer verdict and the run does not move."));

        try
        {
            var before = _library.ObservedRun?.CurrentStageId;

            UiProbe.Invoke(UiProbe.Require<Button>(_window, "LibraryAdvanceRunButton"));
            await WaitUntilAsync(() => !_library.IsBusy, "the advance command finished");
            await SettleAsync();

            var after = _library.ObservedRun?.CurrentStageId;
            var observed = $"Blocker={_library.Blocker}; Stage {before} -> {after}";

            var passed = !string.IsNullOrWhiteSpace(_library.Blocker)
                && _library.Blocker.Contains("verdict", StringComparison.OrdinalIgnoreCase)
                && after == WorkflowScheme.DocumentReviewStageId;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "14-reviewer-gate-refusal");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private void UserApprovalStageBlocked()
    {
        _report.Add(new AcceptanceStep(
            "user-approval",
            "stage-user-approval is not reachable in an isolated run",
            "Attempt the user decision controls of the Workflows screen.",
            "NOT_TESTED: the run cannot leave stage-document-review without a linked live model verdict, so "
            + "the approval stage is never entered.")
            .Complete(
                AcceptanceOutcome.NotTested,
                $"Stage={_library.ObservedRun?.CurrentStageId}; "
                + $"ApproveEnabled={_library.CanApproveObservedArtifact}; "
                + $"RejectEnabled={_library.CanRejectObservedArtifact}; "
                + $"Approver={_library.UserApprovalApproverDisplay}",
                detail:
                "The pinned chain only advances past stage-document-review on verdicts backed by a persisted, "
                + "succeeded, read-only reviewer execution with an observed route. The reviewer channel "
                + "(star-cliproxy-review-readonly) refuses every route in this build and the harness is not "
                + "allowed to mint a reviewer execution, a session, a verdict or a fake backend success. The "
                + "missing evidence is a live model verdict linked to a stored DocumentBundle; the missing "
                + "control is nothing - the product's approval panel is present and unoffered on this stage."));
    }

    private async Task ConsoleStudioPinnedGraphAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "console-studio-pinned",
            "Console Studio shows the pinned run graph next to the editable template",
            "Open the Workflow Console and read the Studio overview.",
            "The run graph names the observed run and is a different surface from the editable template."));

        try
        {
            await OpenConsoleAsync();
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleTemplatesButton"));
            await SettleAsync();

            var studio = _shell.WorkflowConsole!.Studio!;
            var observed = $"Template={studio.TemplateGraphSummary}; RunGraph={studio.RunGraphSummary}";

            var passed = studio.HasRunGraph
                && studio.RunGraphSummary.Contains(_runId ?? "-", StringComparison.Ordinal)
                && !studio.RunGraphSummary.Contains(studio.TemplateGraphSummary, StringComparison.Ordinal);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "15-console-studio-pinned");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task MonitorSchemaAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "monitor-schema",
            "Activity Monitor schema reports no unproven fields",
            "Open the console monitor schema panel.",
            "Every node reports 'Not reported' for account, model, native session and observed route, and no "
            + "node is marked parallel read-only."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleSchemaButton"));
            await SettleAsync();

            var monitor = _shell.WorkflowConsole!.Monitor!;
            var nodes = monitor.Nodes.ToArray();

            var observed = nodes.Length == 0
                ? "no nodes"
                : string.Join(
                    " | ",
                    nodes.Select(node =>
                        $"{node.RoleId}: route={node.RouteSummaryDisplay}; model={node.ModelSummaryDisplay}; "
                        + $"account={node.AccountSummaryDisplay}; last={node.LastActivityDisplay}; "
                        + $"parallelReadOnly={node.IsParallelReadOnly}"));

            var passed = nodes.Length > 0
                && !monitor.HasParallelReadOnlyActivity
                && nodes.All(node =>
                    node.RouteSummaryDisplay == NotReported
                    && node.ModelSummaryDisplay == NotReported
                    && node.AccountSummaryDisplay == NotReported
                    && node.LastActivityDisplay == NotReported);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "16-monitor-schema");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task MonitorActivityAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "monitor-activity",
            "Activity timeline lists only what the run actually produced",
            "Switch the console monitor to the activity stream.",
            "The timeline is populated from the stored run and reports 'Not reported' where nothing was observed."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleActivityButton"));
            await SettleAsync();

            var monitor = _shell.WorkflowConsole!.Monitor!;
            var items = monitor.Activity.ToArray();
            var observed = string.Join(
                " | ",
                items.Select(item =>
                    $"{(string.IsNullOrWhiteSpace(item.RouteDisplay) ? NotReported : item.RouteDisplay)}"));

            var passed = items.Length > 0
                && items.All(item => item.RouteDisplay == NotReported)
                && !monitor.HasParallelReadOnlyActivity;

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "17-monitor-activity");

            _report.Add(step.Complete(
                passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                items.Length == 0 ? "no activity items" : $"{items.Length} item(s): {observed}",
                evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task MonitorInspectorDrawerAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "monitor-drawer",
            "Inspector opens the details drawer beside the run",
            "Switch the console monitor to the inspector panel.",
            "The drawer opens on the run's current role and reports 'Not reported' for every unproven field."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "ConsoleInspectorButton"));
            await SettleAsync();

            var monitor = _shell.WorkflowConsole!.Monitor!;
            var drawer = monitor.Drawer;

            var observed =
                $"DrawerOpen={drawer.IsOpen}; Role={drawer.RoleDisplay}; Model={drawer.ModelAccountDisplay}; "
                + $"Evidence={drawer.EvidenceSourceDisplay}";

            var passed = drawer.IsOpen
                && drawer.RoleDisplay != NotReported
                && drawer.ModelAccountDisplay.Contains(NotReported, StringComparison.Ordinal);

            var evidence = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "18-monitor-drawer");

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed, evidence));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private void DeclaredRouteIsNotObservedEvidence()
    {
        var step = _report.Add(new AcceptanceStep(
            "declared-route",
            "The declared route-opencode binding is not reported as observed evidence",
            "Read the Studio role matrix and the Activity Monitor node summaries together.",
            "route-opencode appears only as a declared binding, never as an observed route."));

        try
        {
            var studio = _shell.WorkflowConsole!.Studio!;
            var monitor = _shell.WorkflowConsole!.Monitor!;

            var declared = studio.RoleMatrix
                .Where(item => string.Equals(item.RouteDisplay, "route-opencode", StringComparison.Ordinal))
                .Select(item => item.RoleDisplay)
                .ToArray();

            var observedAsRoute = monitor.Nodes
                .Where(node => node.RouteSummaryDisplay.Contains("route-opencode", StringComparison.Ordinal))
                .Select(node => node.RoleId)
                .ToArray();

            var observed = $"Declared roles bound to route-opencode: [{string.Join(", ", declared)}]; "
                + $"monitor nodes reporting it as an observed route: [{string.Join(", ", observedAsRoute)}]";

            var passed = declared.Length > 0 && observedAsRoute.Length == 0;

            _report.Add(step.Complete(passed ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail, observed));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task LightThemeAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "theme-light",
            "Light theme renders the console and the Workflows screen",
            "Press the shipped light-theme button of the shell and capture both surfaces.",
            "Both surfaces render with the light palette at normal and at doubled raster scale."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "LightThemeButton"));
            await SettleAsync();

            var normal = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "19-light-console-96dpi");
            var high = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "20-light-console-192dpi", 192d);

            _report.Add(step.Complete(
                AcceptanceOutcome.Pass,
                $"Palette=Light; normal={normal}; high={high}",
                normal));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task DarkThemeRestoredAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "theme-dark",
            "Dark theme renders the console and the Workflows screen",
            "Press the shipped dark-theme button of the shell and capture both surfaces.",
            "Both surfaces render with the dark palette at normal and at doubled raster scale."));

        try
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "DarkThemeButton"));
            await SettleAsync();

            var normal = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "21-dark-console-96dpi");
            var high = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "22-dark-console-192dpi", 192d);

            await CloseConsoleAsync();

            var screen = WindowCapture.Capture(_window, _options.ScreenshotDirectory, "23-dark-workflows-screen");

            _report.Add(step.Complete(
                AcceptanceOutcome.Pass,
                $"Palette=Dark; normal={normal}; high={high}; screen={screen}",
                screen));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task ReviewEvidenceRepositoryEmptyAsync()
    {
        var step = _report.Add(new AcceptanceStep(
            "evidence-empty",
            "Production reviewer evidence stayed empty for the whole run",
            "Read the registered IWorkflowReviewEvidenceRepository and the run's stored rows.",
            "No reviewer execution, session, execution or verdict was persisted by this run."));

        try
        {
            _report.RunId = _runId ?? _report.RunId;
            _report.ObservedStageId = _library.ObservedRun?.CurrentStageId ?? _report.ObservedStageId;
            _report.ObservedTemplateDisplay = _library.ObservedRunTemplateDisplay;

            var evidenceRows = await CountReviewEvidenceAsync();
            var sessions = await CountRunSessionsAsync();
            var executions = await CountRunExecutionsAsync();
            var verdicts = await CountRunVerdictsAsync();
            var approvals = (await ReloadRunAsync())?.Approvals.Count ?? 0;

            var observed =
                $"ReviewEvidence={evidenceRows}; Sessions={sessions}; Executions={executions}; "
                + $"Verdicts={verdicts}; Approvals={approvals}";

            var empty = evidenceRows == 0 && sessions == 0 && executions == 0 && verdicts == 0 && approvals == 0;

            _report.ProductionReviewEvidenceEmpty = empty;
            _report.ReviewEvidenceNote = observed;

            _report.Add(step.Complete(
                empty ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                observed,
                null,
                "The registered production repository is the only one used; the harness registers no reviewer "
                + "repository of its own."));
        }
        catch (Exception exception)
        {
            _report.Add(step.Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }
    }

    private async Task NavigateToWorkflowsAsync()
    {
        if (_shell.ActiveScreenId != ScreenId.Workflows)
        {
            UiProbe.InvokeShortcut(_window, Key.D8, ModifierKeys.Control);
            await SettleAsync();
        }
    }

    private async Task OpenConsoleAsync()
    {
        if (!_shell.IsWorkflowConsoleOpen)
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "OpenWorkflowConsoleButton"));
            await SettleAsync();
        }
    }

    private async Task CloseConsoleAsync()
    {
        if (_shell.IsWorkflowConsoleOpen)
        {
            UiProbe.Invoke(UiProbe.Require<Button>(_window, "CloseWorkflowConsoleButton"));
            await SettleAsync();
        }
    }

    /// <summary>Types a path into the shipped text box and presses the shipped attach button.</summary>
    private async Task AttachAsync(string path)
    {
        var box = UiProbe.Require<TextBox>(_window, "LibraryStageArtifactPathBox");

        if (!box.IsEnabled)
        {
            throw new InvalidOperationException("The shipped artifact path box is frozen for this observation.");
        }

        box.Text = path;
        System.Windows.Data.BindingOperations
            .GetBindingExpression(box, System.Windows.Controls.TextBox.TextProperty)
            ?.UpdateSource();

        await SettleAsync();

        UiProbe.Invoke(UiProbe.Require<Button>(_window, "LibraryAttachStageArtifactButton"));
        await WaitUntilAsync(() => !_library.IsBusy, "the attach command finished");
        await SettleAsync();
    }

    private static async Task SettleAsync()
    {
        await WindowCapture.SettleAsync();
        await WindowCapture.SettleAsync();
    }

    /// <summary>
    /// Pumps the dispatcher until the product command settled. Every awaited continuation returns to the
    /// WPF dispatcher, so the window keeps painting while a command is in flight.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {description}.");
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    private async Task<LLMWorkGUI.Domain.Entities.WorkflowRun?> ReloadRunAsync()
    {
        if (_runId is null)
        {
            return null;
        }

        var repository = _services.GetRequiredService<IWorkflowRunRepository>();
        return await repository.GetByIdAsync(_runId).ConfigureAwait(true);
    }

    private async Task<int> CountRunsAsync()
    {
        var runs = await _services.GetRequiredService<IWorkflowRunRepository>()
            .GetByProjectIdAsync(AcceptanceSeed.ProjectId)
            .ConfigureAwait(true);

        return runs.Count;
    }

    private async Task<int> CountArtifactsAsync()
    {
        var run = await ReloadRunAsync().ConfigureAwait(true);
        return run?.Artifacts.Count ?? 0;
    }

    private async Task<int> CountReviewEvidenceAsync()
    {
        if (_runId is null)
        {
            return 0;
        }

        var evidence = await _services.GetRequiredService<IWorkflowReviewEvidenceRepository>()
            .ListByRunIdAsync(_runId)
            .ConfigureAwait(true);

        return evidence.Count;
    }

    private async Task<int> CountRunSessionsAsync()
    {
        if (_runId is null)
        {
            return 0;
        }

        var sessions = await _services.GetRequiredService<ISessionRepository>()
            .ListByProjectAsync(AcceptanceSeed.ProjectId)
            .ConfigureAwait(true);

        return sessions.Count(session => string.Equals(session.WorkflowRunId, _runId, StringComparison.Ordinal));
    }

    private async Task<int> CountRunExecutionsAsync()
    {
        if (_runId is null)
        {
            return 0;
        }

        var sessions = await _services.GetRequiredService<ISessionRepository>()
            .ListByProjectAsync(AcceptanceSeed.ProjectId)
            .ConfigureAwait(true);

        var repository = _services.GetRequiredService<IExecutionRepository>();
        var total = 0;

        foreach (var session in sessions)
        {
            var executions = await repository.ListBySessionAsync(session.Id).ConfigureAwait(true);
            total += executions.Count;
        }

        return total;
    }

    private async Task<int> CountRunVerdictsAsync()
    {
        var run = await ReloadRunAsync().ConfigureAwait(true);
        return run?.Verdicts.Count ?? 0;
    }

    private async Task<bool> BlobVerifiesAsync(string expectedHash)
    {
        var blobStore = _services.GetRequiredService<WorkflowBlobStore>();
        return await blobStore.VerifyBlobAsync(expectedHash).ConfigureAwait(true);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
