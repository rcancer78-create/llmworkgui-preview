using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Keyboard focus isolation")]
public sealed class WorkflowAdaptationModalFocusReviewTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task AdaptationPreviewOwnsKeyboardTraversalAndEscapeRestoresTheLibraryInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var library = new WorkflowLibraryViewModel(new InMemoryWorkflowPackageRepository(), new InMemoryWorkflowVersionRepository())
            { RoutePolicyId = "owned unchanged policy" };
            var host = new ContentControl { Content = library };
            var window = new Window { Width = 1280, Height = 800, Content = host, ShowActivated = true, WindowStyle = WindowStyle.None };
            foreach (var file in new[] { "Themes/Shared.xaml", "Views/ScreenTemplates.xaml" })
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/" + file, UriKind.Absolute) });
            window.Show();
            window.Activate();
            try
            {
                Settle(host);
                var invoker = Assert.Single(Descendants<TextBox>(host).Where(x =>
                    BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path?.Path == "RoutePolicyId"));
                invoker.BringIntoView();
                Settle(host);
                Assert.True(invoker.Focus(), "The actual library policy field must accept initial focus.");
                Pump();
                Assert.Same(invoker, Keyboard.FocusedElement);

                const string hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var now = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
                var package = new WorkflowPackageItemViewModel(new WorkflowPackage("owned-package", "Owned", "Owned focus fixture", [], WorkflowSourceType.ZipArchive, hash, hash, now, now));
                var version = new WorkflowVersionItemViewModel(new WorkflowVersion("owned-version", "owned-package", 1, hash, hash, WorkflowSourceType.ZipArchive, null, null, null, null, null, now, null));
                var project = new Project("owned-project", "Owned", @"D:\work\owned-focus-fixture", null, false, true, null, null, DataClassification.PrivateSource);
                await library.AdaptationDialog.OpenForVersionAsync(version, package, project);
                Settle(host);
                var overlay = Assert.Single(Descendants<Grid>(host).Where(x =>
                    BindingOperations.GetBinding(x, UIElement.VisibilityProperty)?.Path?.Path == "AdaptationDialog.IsVisible"));
                Assert.True(overlay.IsVisible && library.AdaptationDialog.IsVisible);
                Assert.True(IsInside(overlay, Keyboard.FocusedElement), "Opening the actual adaptation preview must move focus out of the covered library field.");

                var reached = new HashSet<IInputElement>();
                foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
                {
                    for (var hop = 0; hop < 32; hop++)
                    {
                        var current = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
                        current.MoveFocus(new TraversalRequest(direction));
                        Pump();
                        var focused = Keyboard.FocusedElement;
                        Assert.True(IsInside(overlay, focused), $"{direction} traversal escaped the adaptation preview at step {hop}.");
                        reached.Add(Assert.IsAssignableFrom<IInputElement>(focused));
                    }
                }
                Assert.True(reached.Count > 1, "Traversal must actually reach different dialog controls.");
                Assert.Equal("owned unchanged policy", library.RoutePolicyId);
                Assert.Same(version, library.AdaptationDialog.SourceVersion);

                var focus = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                var source = PresentationSource.FromVisual(window);
                Assert.NotNull(source);
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                focus.RaiseEvent(escape);
                Settle(host);
                Assert.True(escape.Handled, "The actual preview-key handler must handle Escape.");
                Assert.False(library.AdaptationDialog.IsVisible);
                Assert.False(overlay.IsVisible);
                Assert.Same(invoker, Keyboard.FocusedElement);
                Assert.Equal("owned unchanged policy", library.RoutePolicyId);
            }
            finally { window.Close(); }
        });
    }

    private static bool IsInside(DependencyObject scope, IInputElement? element)
    {
        for (var current = element as DependencyObject; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, scope)) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Settle(FrameworkElement root) { root.UpdateLayout(); Pump(); root.UpdateLayout(); Pump(); }
    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }
}
