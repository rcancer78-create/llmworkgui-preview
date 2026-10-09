using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition and shell integration of the Phase 11B Activity Center. The unified shell must expose
/// the overlay, the command palette must offer the action, and the DI registration must build the
/// search index on top of <see cref="SensitiveDataFilter.Redact"/>.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class ActivityCenterShellIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActivityCenterServices_AreRegisteredOnTopOfTheRedactingIndex()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(TimeProvider.System);
        services.AddUnifiedWorkspaceShell();

        using var provider = services.BuildServiceProvider();

        var index = provider.GetRequiredService<IEventSearchIndex>();
        var service = provider.GetRequiredService<IActivityCenterService>();
        var viewModel = provider.GetRequiredService<ActivityCenterViewModel>();

        Assert.NotNull(index);
        Assert.NotNull(service);
        Assert.NotNull(viewModel);

        const string apiKey = "sk-composition-1234567890";
        service.Append(new ActivityEvent(
            "event-secret",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.TechLead,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "Composition check",
            $"api_key={apiKey}"));

        Assert.Empty(index.Search(apiKey));
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = apiKey }).Items);
        Assert.Single(service.Query().Items);
    }

    /// <summary>
    /// The index and the ingestion boundary must share one bound, composed once.
    /// <para>
    /// They used to default to the same 100 000 through two separate constants, so raising only the
    /// service's bound - which is exactly what the load driver did - left 90 000 events retained and
    /// pageable but not searchable, and nothing complained. Asserting the two capacities are the same
    /// number makes that substitution impossible to express.
    /// </para>
    /// </summary>
    [Fact]
    public void TheInMemoryIndexAndTheIngestionBoundaryShareOneBound()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(TimeProvider.System);
        services.AddUnifiedWorkspaceShell();

        using var provider = services.BuildServiceProvider();

        var index = provider.GetRequiredService<IEventSearchIndex>();
        var service = provider.GetRequiredService<IActivityCenterService>();

        Assert.Equal(ActivityCenterService.DefaultCapacity, index.Capacity);
        Assert.Equal(index.Capacity, service.Capacity);
    }

    [Fact]
    public void Shell_OpensTheActivityCenterOverlay_AndThePaletteOffersTheAction()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var activityCenter = CreateActivityCenterViewModel();
        var shell = new UnifiedWorkspaceShellViewModel(
            provider.GetRequiredService<MainWindowViewModel>(),
            new StubLayoutPersistenceService(),
            activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var view = new UnifiedWorkspaceShellView { DataContext = shell };

            var window = new Window
            {
                Width = 1280,
                Height = 800,
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

            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();

            try
            {
                // The palette carries the new action.
                Assert.Contains(
                    shell.CommandPalette.Items,
                    item => item.Id == "actions.activity-center" && item.Category == CommandPaletteCategories.Actions
                        && item.Title == "Открыть центр активности"
                        && item.Description.StartsWith("Поиск и фильтрация", StringComparison.Ordinal));

                // The overlay starts closed and the shell-provided VM is the same instance.
                Assert.False(shell.IsActivityCenterOpen);
                Assert.Same(activityCenter, shell.ActivityCenter);

                var overlay = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("ActivityCenterOverlay"));
                Assert.False(overlay.IsVisible);

                var button = Assert.IsAssignableFrom<System.Windows.Controls.Button>(view.FindName("OpenActivityCenterButton"));
                Assert.NotNull(button.Command);

                shell.OpenActivityCenterCommand.Execute(null);
                view.UpdateLayout();

                Assert.True(shell.IsActivityCenterOpen);
                Assert.True(overlay.IsVisible);

                var texts = ReadTexts(view);

                Assert.Contains("Центр активности", texts);
                Assert.Contains("Shell integration event", texts);

                shell.CloseActivityCenterCommand.Execute(null);
                view.UpdateLayout();

                Assert.False(shell.IsActivityCenterOpen);
                Assert.False(overlay.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ActivityCenterViewModel CreateActivityCenterViewModel()
    {
        var service = new ActivityCenterService(new SensitiveDataFilter().Redact, new FixedTimeProvider(Now));

        service.Append(new ActivityEvent(
            "event-1",
            Now.AddMinutes(-5),
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Completed,
            ActivityEventSource.Native,
            "Shell integration event",
            "Rendered inside the unified workspace shell overlay."));

        return new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new SilentClipboard());
    }

    private static string[] ReadTexts(DependencyObject root) =>
        EnumerateVisuals(root)
            .Select(visual => visual switch
            {
                System.Windows.Controls.TextBlock block when !string.IsNullOrWhiteSpace(block.Text) => block.Text,
                System.Windows.Controls.TextBox box when !string.IsNullOrWhiteSpace(box.Text) => box.Text,
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

    private sealed class StubLayoutPersistenceService : ILayoutPersistenceService
    {
        public ShellLayoutState State { get; set; } = new();

        public ShellLayoutState Load() => State;

        public void Save(ShellLayoutState state) => State = state;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class SilentClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }
}
