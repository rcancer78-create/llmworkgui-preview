using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition-root evidence for the Mirasim panel. A green view-model test is meaningless if the
/// shipped XAML never receives the panel, so the production registration is asserted here instead of
/// being assumed.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class MirasimWorkspaceCompositionTests
{
    [Fact]
    public void AddAppUi_RegistersTheMirasimPanelAndInjectsItIntoTheWorkspaceScreen()
    {
        using var provider = UiTestHost.CreateProvider();

        var workspace = provider.GetRequiredService<WorkspaceViewModel>();

        // Without this the shipped XAML collapses the panel and the whole Mirasim surface is
        // unreachable for a real user, no matter how green the view-model tests are.
        Assert.True(workspace.HasMirasimPanel);
        Assert.NotNull(workspace.Mirasim);
        Assert.Same(provider.GetRequiredService<MirasimWorkspaceViewModel>(), workspace.Mirasim);
    }

    [Fact]
    public void AddAppUi_ComposesTheMirasimBackendForThePanel()
    {
        using var provider = UiTestHost.CreateProvider();

        var panel = provider.GetRequiredService<MirasimWorkspaceViewModel>();

        Assert.True(panel.IsBackendAvailable);
        Assert.Equal($"http://{MirasimOptions.DefaultHostname}:{MirasimOptions.DefaultPort}/", panel.HostUrl);
        Assert.True(panel.CanProbeHost);
        Assert.Equal(MirasimWorkspaceViewModel.NotProbedState, panel.HostStateDisplay);

        // A composed backend without a successful probe must not offer a session.
        Assert.False(panel.CanCreateSession);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, panel.InstanceId);
    }

    [Fact]
    public void AddAppUi_ConsumesAnAvailableHealthCenterService()
    {
        // With a health service registered, the panel must actually consume it: an optional dependency
        // that is never resolved would make the health-adapter tests meaningless.
        using var provider = UiTestHost.CreateProvider(
            healthCenter: new HealthCenterService(
                new InMemoryHealthStateStore(),
                new InMemoryHealthEventStore(),
                new HealthUiTimeProvider()));

        var panel = provider.GetRequiredService<MirasimWorkspaceViewModel>();

        Assert.True(panel.IsHealthReportingAvailable);
    }

    [Fact]
    public void AddAppUi_WithoutAHealthCenter_ReportsTheAdapterAsUnavailable()
    {
        using var provider = UiTestHost.CreateProvider();

        var panel = provider.GetRequiredService<MirasimWorkspaceViewModel>();

        Assert.False(panel.IsHealthReportingAvailable);
    }

    [Fact]
    public void ShippedXaml_RendersTheComposedMirasimPanelThroughTheProductionTemplate()
    {
        // The panel is rendered through the production DataTemplate, loaded exactly as MainWindow loads
        // it, so the test exercises the shipped XAML rather than a test-only copy.
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MirasimWorkspaceViewModel>();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenPanel(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("Mirasim", texts);
                Assert.Contains(MirasimWorkspaceViewModel.NotProbedState, texts);
                Assert.Contains(MirasimRouteModes.ManualOnly, texts);

                // The fail-closed approvals and raw-recording notices are permanent.
                Assert.Contains(MirasimWorkspaceViewModel.ApprovalsNotice, texts);
                Assert.Contains(MirasimWorkspaceViewModel.RecordingNotice, texts);
                Assert.Contains(MirasimWorkspaceViewModel.RoutePolicyNotice, texts);

                // The harness selector and the model input are the real controls, not decorative text.
                Assert.Contains(
                    FindVisualDescendants<ComboBox>(root),
                    combo => combo.SelectedItem as string == MirasimWorkspaceViewModel.DefaultHarness);
                Assert.Contains(
                    FindVisualDescendants<TextBox>(root),
                    textBox => textBox.Text == MirasimWorkspaceViewModel.DefaultModelId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MirasimPanel_WithoutTheBackend_ReportsUnavailableInsteadOfFakingASession()
    {
        // The panel can be constructed without any backend, and it must then degrade honestly rather
        // than present an unusable-but-enabled surface.
        var panel = new MirasimWorkspaceViewModel();

        Assert.False(panel.IsBackendAvailable);
        Assert.False(panel.CanProbeHost);
        Assert.False(panel.CanCreateSession);
        Assert.False(panel.CanSendPrompt);
        Assert.Equal(MirasimWorkspaceViewModel.NotProbedState, panel.HostStateDisplay);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, panel.InstanceId);

        // The fail-closed approvals and raw-recording notices are permanent, not backend-dependent.
        Assert.False(string.IsNullOrWhiteSpace(panel.ApprovalsNoticeText));
        Assert.False(string.IsNullOrWhiteSpace(panel.RecordingNoticeText));
    }

    private static (Window Window, FrameworkElement Root) OpenPanel(MirasimWorkspaceViewModel viewModel)
    {
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 900,
            Height = 620,
            Content = host,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml",
                UriKind.Absolute)
        });

        window.Show();

        host.Measure(new Size(900, 620));
        host.Arrange(new Rect(0, 0, 900, 620));
        host.UpdateLayout();

        return (window, host);
    }

    private static string[] ReadTextBlocks(DependencyObject root) =>
        FindVisualDescendants<TextBlock>(root)
            .Select(textBlock => textBlock.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
