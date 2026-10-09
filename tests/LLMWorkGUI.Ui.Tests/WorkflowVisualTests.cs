using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed class WorkflowVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly string DocumentHash = "sha256:" + new string('a', 64);
    private static readonly string UiEvidenceHash = "sha256:" + new string('b', 64);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Workflow_ApprovalDialog_ShowsTheEvidenceAndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var approvalNode = new WorkflowNodeDefinition(
            "approval-gate",
            WorkflowNodeKind.ApprovalGate,
            "Approve the implementation diff",
            "Approver",
            successTargetNodeId: "terminal");

        var verdicts = new[]
        {
            new ReviewerVerdictRecord(
                "ReviewerLevel1",
                "route-opencode",
                DocumentHash,
                WorkflowReviewVerdict.Approve,
                "The diff stays within the declared scope.",
                RecordedAt),
            new ReviewerVerdictRecord(
                "ReviewerLevel2",
                "route-cursor",
                DocumentHash,
                WorkflowReviewVerdict.Approve,
                "The captured UI evidence is pinned to the document hash.",
                RecordedAt)
        };

        var decision = CreateCodingRule().Evaluate(new CodingStageTransitionEvidence(
            new[] { "TechnicalSpecification" },
            DocumentHash,
            verdicts,
            diffWithinScope: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash));

        var verdictRows = string.Join(
            Environment.NewLine,
            verdicts.Select(verdict =>
                $"<TextBlock Text=\"{Escape($"{verdict.ReviewerRole}: {verdict.Verdict} via {verdict.RouteId}")}\" "
                + "Foreground=\"#FFB5EAD7\" Margin=\"0,2,0,0\" />"));

        var xaml = $$"""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="#FF1B1B1F" Padding="24">
              <StackPanel>
                <TextBlock Text="User approval required" FontSize="22" FontWeight="SemiBold"
                           Foreground="#FFF2F2F2" />
                <TextBlock Text="{{Escape(approvalNode.DisplayName)}}" FontSize="16"
                           Foreground="#FFD6D6D6" Margin="0,6,0,0" />
                <TextBlock Text="Node: {{approvalNode.NodeId}} · Role: {{approvalNode.RoleBinding}}"
                           Foreground="#FF9E9E9E" Margin="0,2,0,0" />
                <TextBlock Text="Document hash: {{DocumentHash}}" Foreground="#FF9E9E9E" Margin="0,8,0,0" />
                <TextBlock Text="Separate reviewer verdicts (never folded into an anonymous PASS)"
                           FontSize="14" FontWeight="SemiBold" Foreground="#FFF2F2F2" Margin="0,12,0,0" />
                {{verdictRows}}
                <TextBlock Text="Transition check: {{(decision.IsAllowed ? "allowed" : "blocked")}}"
                           Foreground="#FFB5EAD7" Margin="0,12,0,0" />
                <TextBlock Text="This decision is recorded as separate user approval evidence; it is never a tool approval."
                           Foreground="#FF9E9E9E" Margin="0,12,0,0" TextWrapping="Wrap" />
                <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
                  <Button Content="Approve" Padding="16,6" Margin="0,0,12,0" />
                  <Button Content="Reject" Padding="16,6" Margin="0,0,12,0" />
                  <Button Content="Request changes" Padding="16,6" />
                </StackPanel>
              </StackPanel>
            </Border>
            """;

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var root = (FrameworkElement)XamlReader.Parse(xaml);
            var window = OpenWindow(root);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("User approval required", texts);
                Assert.Contains(approvalNode.DisplayName, texts);
                Assert.Contains(texts, text => text.Contains(DocumentHash, StringComparison.Ordinal));
                Assert.Contains("ReviewerLevel1: Approve via route-opencode", texts);
                Assert.Contains("ReviewerLevel2: Approve via route-cursor", texts);
                Assert.Contains("Approve", texts);
                Assert.Contains("Reject", texts);
                Assert.Contains("Request changes", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("never a tool approval", StringComparison.OrdinalIgnoreCase));
                Assert.Contains("Transition check: allowed", texts);

                var screenshotPath = CaptureScreenshot(root, "workflow_approval_dialog.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Workflow_NodeTransitionsAndRoleTimeline_GenerateScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var planService = new WorkflowExecutionPlanService();
        var plan = planService.CreatePlan(CreateWorkflowGraph());

        planService.AdvanceSuccess(plan);
        Assert.Equal("writer", plan.CurrentNodeId);

        planService.AdvanceFailure(plan, "Review returned request-changes.");
        Assert.Equal("retry", plan.CurrentNodeId);

        planService.AdvanceSuccess(plan);
        Assert.Equal("writer", plan.CurrentNodeId);

        planService.AdvanceSuccess(plan);
        Assert.Equal("review", plan.CurrentNodeId);

        var roleBindings = new[]
        {
            new RoleBindingDefinition("Architect", "route-opencode"),
            new RoleBindingDefinition("Coder", "route-opencode", new[] { "route-cursor" }),
            new RoleBindingDefinition("ReviewerLevel1", "route-cursor"),
            new RoleBindingDefinition("Approver")
        };

        var coderBinding = roleBindings.Single(binding => binding.RoleId == "Coder");
        var routeChange = coderBinding.EvaluateRouteChange(
            "route-opencode",
            "route-cursor",
            "Retry budget exhausted; escalation moved the role explicitly.");

        var stepRows = string.Join(
            Environment.NewLine,
            plan.Steps.Select((step, index) =>
                $"<TextBlock Text=\"{Escape($"{index + 1}. {step.NodeId} ({step.Kind})")}\" "
                + "FontWeight=\"SemiBold\" Foreground=\"#FFF2F2F2\" Margin=\"0,4,0,0\" />"
                + $"<TextBlock Text=\"{Escape(step.Reason)}\" Foreground=\"#FF9E9E9E\" />"));

        var roleRows = string.Join(
            Environment.NewLine,
            roleBindings.Select(binding =>
                $"<TextBlock Text=\"{Escape($"{binding.RoleId} → {string.Join(" / ", binding.AllowedRouteIds)}")}\" "
                + "Foreground=\"#FFB5EAD7\" Margin=\"0,2,0,0\" />"));

        var xaml = $$"""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="#FF1B1B1F" Padding="24">
              <StackPanel>
                <TextBlock Text="Workflow node transitions" FontSize="22" FontWeight="SemiBold"
                           Foreground="#FFF2F2F2" />
                <TextBlock Text="Current node: {{plan.CurrentNodeId}} · Complete: {{plan.IsComplete}}"
                           Foreground="#FFD6D6D6" Margin="0,4,0,0" />
                {{stepRows}}
                <TextBlock Text="Role timeline" FontSize="18" FontWeight="SemiBold"
                           Foreground="#FFF2F2F2" Margin="0,16,0,0" />
                {{roleRows}}
                <TextBlock Text="Route change: {{routeChange.BoundRouteId}} → {{routeChange.RequestedRouteId}} ({{Escape(routeChange.Reason!)}})"
                           Foreground="#FFF7B267" Margin="0,12,0,0" TextWrapping="Wrap" />
                <TextBlock Text="Requires a new backend session: {{routeChange.RequiresNewNativeSession}} · native session carried: {{routeChange.CarriesNativeSession}}"
                           Foreground="#FF9E9E9E" Margin="0,4,0,0" TextWrapping="Wrap" />
                <TextBlock Text="RouteMismatch is reported instead of success when the observed route differs."
                           Foreground="#FF9E9E9E" Margin="0,8,0,0" TextWrapping="Wrap" />
              </StackPanel>
            </Border>
            """;

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var root = (FrameworkElement)XamlReader.Parse(xaml);
            var window = OpenWindow(root);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains(texts, text => text.Contains("Workflow node transitions", StringComparison.Ordinal));
                Assert.Contains("Current node: review · Complete: False", texts);
                Assert.Contains(texts, text => text.Contains("1. prompt (Prompt)", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("2. writer (Writer)", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("3. retry (Retry)", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("Review returned request-changes.", StringComparison.Ordinal));
                Assert.Contains("Role timeline", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("Coder → route-opencode / route-cursor", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains(
                        "Route change: route-opencode → route-cursor",
                        StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains(
                        "Requires a new backend session: True · native session carried: False",
                        StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(root, "workflow_node_transitions.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CodingStageTransitionRule CreateCodingRule() =>
        new(
            "code-and-ui",
            new[] { "TechnicalSpecification" },
            new[] { "ReviewerLevel1", "ReviewerLevel2" },
            requiresDiffScopeCheck: true,
            requiresTestEvidence: false,
            requiresUiEvidence: true,
            fixLimit: 1,
            escalationTargetNodeId: "escalation");

    private static WorkflowGraph CreateWorkflowGraph() =>
        new(
            "prompt",
            new[]
            {
                CreateNode("prompt", WorkflowNodeKind.Prompt, "Architect", success: "writer"),
                CreateNode("writer", WorkflowNodeKind.Writer, "Coder", success: "review", failure: "retry"),
                CreateNode("review", WorkflowNodeKind.Review, "ReviewerLevel1", success: "approval", failure: "retry"),
                CreateNode(
                    "retry",
                    WorkflowNodeKind.Retry,
                    "Coder",
                    success: "writer",
                    failure: "escalation",
                    retryBudget: 2),
                CreateNode("escalation", WorkflowNodeKind.Escalation, "Coder", success: "terminal", failure: "terminal"),
                CreateNode("approval", WorkflowNodeKind.ApprovalGate, "Approver", success: "terminal"),
                CreateNode("terminal", WorkflowNodeKind.TerminalOutcome, "Coordinator")
            });

    private static WorkflowNodeDefinition CreateNode(
        string nodeId,
        WorkflowNodeKind kind,
        string role,
        string? success = null,
        string? failure = null,
        int retryBudget = 0) =>
        new(
            nodeId,
            kind,
            $"Display {nodeId}",
            role,
            successTargetNodeId: success,
            failureTargetNodeId: failure,
            retryBudget: retryBudget);

    private static Window OpenWindow(FrameworkElement root)
    {
        var window = new Window
        {
            Width = 1000,
            Height = 640,
            Content = root,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Show();
        root.Measure(new Size(1000, 640));
        root.Arrange(new Rect(0, 0, 1000, 640));
        root.UpdateLayout();

        return window;
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(1000, root.ActualWidth);
        var height = (int)Math.Max(640, root.ActualHeight);

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

    private static string[] ReadTextBlocks(DependencyObject root) =>
        EnumerateVisuals(root)
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
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

    private static string Escape(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
