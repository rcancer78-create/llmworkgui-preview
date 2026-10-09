using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

// Real WPF focus/traversal on the shipped template. No physical keys, native calls or persistence.
[Collection("Keyboard focus isolation")]
[Trait("Category", "VisualUi")]
public sealed class ProviderConfigPreviewKeyboardReviewTests
{
    [Fact]
    public void PublicPreviewOwnsKeyboardTraversalAndEscapeRestoresItsInvoker()
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var model = new ProvidersAccountsViewModel(provider.GetRequiredService<CliStatusViewModel>(),
                profileRepository: provider.GetRequiredService<IProviderProfileRepository>(),
                configService: new OpenCodeConfigService())
            {
                EditingProviderId = "owned-preview-focus",
                EditingDisplayName = "Owned preview focus",
                EditingBaseUrl = "http://127.0.0.1:1/v1"
            };
            var content = new ContentControl { Content = model };
            var window = new Window { Content = content, Width = 1280, Height = 800,
                WindowStyle = WindowStyle.None, ShowActivated = true };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml")
            });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml")
            });
            try
            {
                window.Show(); window.Activate(); Settle(content);
                var invoker = Assert.Single(Descendants<Button>(content),
                    button => ReferenceEquals(button.Command, model.PreviewConfigCommand));
                Assert.True(invoker.IsVisible && invoker.IsEnabled && invoker.Focusable);
                Assert.True(invoker.Focus()); Settle(content);
                Assert.Same(invoker, Keyboard.FocusedElement);
                Assert.True(model.PreviewConfigCommand.CanExecute(null));
                model.PreviewConfigCommand.Execute(null);
                Settle(content);
                Assert.True(model.PreviewDialog.IsVisible);
                var overlay = Assert.Single(Descendants<Grid>(content), grid =>
                    BindingOperations.GetBinding(grid, UIElement.VisibilityProperty)?.Path?.Path == "PreviewDialog.IsVisible");
                Assert.True(overlay.IsVisible);
                Assert.True(IsInside(overlay, Keyboard.FocusedElement),
                    "Opening the actual public preview must transfer keyboard focus off the underlying provider form.");

                foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
                    for (var hop = 0; hop < 8; hop++)
                    {
                        var focused = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                        Assert.True(focused.MoveFocus(new TraversalRequest(direction)));
                        Settle(content);
                        Assert.True(IsInside(overlay, Keyboard.FocusedElement),
                            "Tab/Shift+Tab traversal must not reach the enabled form hidden behind the preview.");
                    }
                var current = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                current.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(current),
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Settle(content);
                Assert.False(model.PreviewDialog.IsVisible);
                Assert.Same(invoker, Keyboard.FocusedElement);
            }
            finally { model.PreviewDialog.Close(); window.Close(); }
        });
    }

    private static void Settle(FrameworkElement content)
    {
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        content.UpdateLayout();
    }

    private static bool IsInside(DependencyObject scope, IInputElement? input)
    {
        for (var current = input as DependencyObject; current is not null;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, scope)) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
