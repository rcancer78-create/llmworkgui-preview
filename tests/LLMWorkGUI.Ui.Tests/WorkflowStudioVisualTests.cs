using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
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
/// QuickViewer-style WPF scenarios of the Phase 10E Workflow Studio. The shipped
/// <see cref="WorkflowStudioView"/> is rendered over realistic template and document state: the editable
/// template schema next to the actual run graph, and the document approval surface with hashes,
/// separate reviewer verdicts and the pre-coder gate checks. Screenshots are persisted.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowStudioVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectedTemplateNode_ShowsEditableNodeFields()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var vm = CreateViewModel();
            vm.SelectedNode = vm.TemplateNodes.First();
            var (window, view) = OpenView(vm);
            try
            {
                var editor = Assert.Single(EnumerateVisuals(view).OfType<TextBox>().Where(box =>
                    System.Windows.Data.BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "NodeId"));
                Assert.True(editor.IsVisible);
                Assert.Equal(vm.SelectedNode.NodeId, editor.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WorkflowStudio_TemplateEditorShowsEditableSchemaAndActualRunGraph_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateViewModel();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Workflow Studio", texts);
                Assert.Contains("Редактируемая схема шаблона", texts);
                Assert.Contains("Фактический граф выполняемого run", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("Standard development workflow", StringComparison.Ordinal)
                        && text.Contains("built-in", StringComparison.Ordinal));

                // The editable schema lists the template nodes and the validation controls.
                Assert.Contains("stage-task-specification", texts);
                Assert.Contains("stage-code-and-ui", texts);
                Assert.Contains("Проверить граф", texts);
                Assert.Contains(texts, text => text.Contains("node(s), entry", StringComparison.Ordinal));

                // The run graph is the actual, separate stage chain of the run.
                Assert.Contains(texts, text => text.Contains("Постановка задачи", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("Архитектура", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("current", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("run-studio-visual", StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(
                    view,
                    "workflow_studio_template_editor.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WorkflowStudio_DocumentApprovalShowsHashesVerdictsAndGateChecks_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateViewModel();

        GenerateGateReadyDocuments(viewModel);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Документы: draft, версия, hash, вердикты", texts);
                Assert.Contains("Шлюз перед кодером", texts);
                Assert.Contains("Проверка секретов / предпросмотр", texts);
                Assert.Contains("Утвердить документ", texts);

                // The current document hash and version are visible.
                Assert.Contains(texts, text => text.StartsWith("sha256:", StringComparison.Ordinal));
                Assert.Contains("v1", texts);

                // Separate reviewer verdicts are shown instead of one folded pass/fail.
                Assert.Contains(
                    texts,
                    text => text.StartsWith("Reviewer: Approve", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.StartsWith("Architect: Approve", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.StartsWith("UiReviewer: Approve", StringComparison.Ordinal));

                // The gate reports each check separately and allows the coder only now.
                Assert.Contains(
                    texts,
                    text => text.Contains("Обязательные документы: OK", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("Единогласный approve ревьюеров: OK", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("Утверждение пользователя: OK", StringComparison.Ordinal));
                Assert.Contains("Кодер может стартовать", texts);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "workflow_studio_document_approval.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static WorkflowStudioViewModel CreateViewModel(WorkflowSecretScanReport? scanReport = null)
    {
        var time = new FakeTimeProvider { UtcNow = Now };
        var scanner = new StubSecretScanner(scanReport ?? WorkflowSecretScanReport.Empty);
        var sequence = 0;

        var studio = new WorkflowStudioService(
            graphValidator: new WorkflowGraphValidator(),
            templateStore: new InMemoryWorkflowTemplateStore(),
            timeProvider: time);

        var documents = new DocumentTemplateService(
            scanner,
            time,
            () => "draft-" + (sequence++),
            new SyntheticStudioApprovalIdentity());

        var viewModel = new WorkflowStudioViewModel(
            studio,
            documents,
            new PreCoderGateValidator(),
            time);

        viewModel.RefreshTemplatesAsync().GetAwaiter().GetResult();
        viewModel.AddTemplateNode();

        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var run = WorkflowRun.Start(
            "run-studio-visual",
            "project-studio",
            "pkg-studio",
            "ver-studio",
            "session-studio",
            scheme.GetRequiredStage(scheme.InitialStageId),
            Now);

        // Both stages the walk leaves are gated by the artifact they produced, so the run records the problem
        // statement and then the architecture before it moves on.
        run = run.WithArtifact(StageArtifact(run.Id, scheme, WorkflowScheme.TaskSpecificationStageId, Now.AddMinutes(2)));
        run.AdvanceTo(
            scheme.GetRequiredStage(WorkflowScheme.TaskSpecificationStageId),
            scheme.GetRequiredStage(WorkflowScheme.ArchitectureStageId),
            "Problem statement delivered",
            Now.AddMinutes(5));

        run = run.WithArtifact(StageArtifact(run.Id, scheme, WorkflowScheme.ArchitectureStageId, Now.AddMinutes(7)));
        run.AdvanceTo(
            scheme.GetRequiredStage(WorkflowScheme.ArchitectureStageId),
            scheme.GetRequiredStage(WorkflowScheme.TechnicalSpecificationStageId),
            "Architecture delivered",
            Now.AddMinutes(10));

        viewModel.LoadRun(run, scheme);

        return viewModel;
    }

    /// <summary>The artifact a stage declares, recorded with the hash of the content that stands behind it.</summary>
    private static WorkflowArtifactEvidence StageArtifact(
        string runId,
        WorkflowScheme scheme,
        string stageId,
        DateTimeOffset createdAtUtc)
    {
        var kind = scheme.GetRequiredStage(stageId).ArtifactRequirement;
        Assert.NotNull(kind);

        var hash = "sha256:" + Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"artifact of '{stageId}'")))
            .ToLowerInvariant();

        return new WorkflowArtifactEvidence(
            $"artifact-{stageId}",
            runId,
            stageId,
            kind,
            hash,
            hash,
            createdAtUtc,
            64,
            DataClassification.PrivateSource);
    }

    private static void GenerateGateReadyDocuments(WorkflowStudioViewModel viewModel)
    {
        foreach (var option in viewModel.DocumentTemplates)
        {
            viewModel.SelectedDocumentTemplate = option;
            viewModel.GenerateDraftAsync().GetAwaiter().GetResult();

            foreach (var role in WorkflowStudioDocumentRules.RequiredReviewerRoles)
            {
                viewModel.ReviewerRole = role;
                viewModel.ReviewerVerdict = WorkflowReviewVerdict.Approve;
                viewModel.AddReviewVerdictAsync().GetAwaiter().GetResult();
            }

            viewModel.ApproveDocumentAsync().GetAwaiter().GetResult();
        }

        viewModel.GateRequestedRouteId = "route-opencode";
        viewModel.GateObservedRouteId = "route-opencode";
        viewModel.RefreshGateAsync().GetAwaiter().GetResult();
    }

    private static (Window Window, WorkflowStudioView View) OpenView(WorkflowStudioViewModel viewModel)
    {
        var view = new WorkflowStudioView { DataContext = viewModel };

        var window = new Window
        {
            Width = 1280,
            Height = 900,
            Content = view,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Show();

        view.Measure(new Size(1280, 900));
        view.Arrange(new Rect(0, 0, 1280, 900));
        view.UpdateLayout();

        return (window, view);
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(1280, root.ActualWidth);
        var height = (int)Math.Max(900, root.ActualHeight);

        var renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        Directory.CreateDirectory(ScreenshotOutputDir);
        var screenshotPath = Path.Combine(ScreenshotOutputDir, fileName);

        ScreenshotFile.Save(encoder, screenshotPath);

        Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");

        return screenshotPath;
    }

    private static string[] ReadTexts(DependencyObject root) =>
        EnumerateVisuals(root)
            .Select(visual => visual switch
            {
                TextBlock block when !string.IsNullOrWhiteSpace(block.Text) => block.Text,
                TextBox box when !string.IsNullOrWhiteSpace(box.Text) => box.Text,
                _ => null
            })
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();

    private static IEnumerable<DependencyObject> EnumerateVisuals(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            yield return child;

            foreach (var descendant in EnumerateVisuals(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class StubSecretScanner : IWorkflowSecretScanner
    {
        private readonly WorkflowSecretScanReport _report;

        public StubSecretScanner(WorkflowSecretScanReport report)
        {
            _report = report;
        }

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The preview only scans in-memory drafts.");

        public Task<WorkflowSecretScanReport> ScanFilesAsync(
            IReadOnlyDictionary<string, byte[]> files,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_report);
    }
}
