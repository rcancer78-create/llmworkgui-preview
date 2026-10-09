using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Bounded keyboard-focus acceptance for the shipped unified shell, command palette and Activity Center.
///
/// <para>
/// The existing visual tests assert that controls are <c>Focusable</c> and that shortcuts are declared.
/// Neither says anything about where keyboard focus actually lands, so this suite drives the real WPF
/// focus system on a shown, activated window and reads <see cref="Keyboard.FocusedElement"/>.
///
/// </para>
/// <para>
/// Honest scope: <see cref="UIElement.MoveFocus(TraversalRequest)"/> is the same traversal engine the Tab
/// and Shift+Tab keys run, and executing the declared Escape/close command is the same handler the physical
/// key raises. No physical key is injected here, no screen reader is attached and no owner has signed this
/// off, so this proves bounded traversal only.
/// </para>
/// </summary>
[Collection("Keyboard focus isolation")]
[Trait("Category", "VisualUi")]
public sealed class KeyboardFocusAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private const int TraversalHops = 8;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsoleAndOnboarding_TrapFocusAndEscapeRestoresInvoker(bool onboarding)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        using var activity = CreateActivityCenterViewModel();
        StaTestRunner.Run(() =>
        {
            var shell = new UnifiedWorkspaceShellViewModel(
                provider.GetRequiredService<MainWindowViewModel>(), new StubLayoutPersistenceService(), activity,
                new LLMWorkGUI.App.ViewModels.Onboarding.OnboardingViewModel(),
                new WorkflowConsolidatedViewModel(new WorkflowLibraryViewModel()));
            var (window, view) = OpenShell(shell, 1280, 800);
            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "overlay invoker");
                if (onboarding) shell.OpenOnboarding(); else shell.OpenWorkflowConsole();
                Settle(view);
                var overlay = Assert.IsAssignableFrom<FrameworkElement>(view.FindName(
                    onboarding ? "OnboardingOverlay" : "WorkflowConsoleOverlay"));
                var scope = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(overlay));
                Assert.True(IsInside(scope, Keyboard.FocusedElement));
                var current = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
                for (var hop = 0; hop < TraversalHops; hop++)
                {
                    current = MoveFocusFrom(current, FocusNavigationDirection.Next);
                    Assert.True(IsInside(scope, current));
                }
                current.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(current),
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Settle(view);
                Assert.False(onboarding ? shell.IsOnboardingOpen : shell.IsWorkflowConsoleOpen);
                Assert.Same(invoker, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteOpenMovesKeyboardFocusIntoTheSearchBox(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                Assert.Equal("Открыть палитру команд", AutomationProperties.GetName(invoker));
                FocusFromShell(invoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);

                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var searchBox = Assert.IsType<TextBox>(palette.FindName("SearchBox"));
                Assert.Equal("Поиск команд", AutomationProperties.GetName(searchBox));
                Assert.True(searchBox.IsKeyboardFocusWithin, Failure(
                    "opening the palette must move keyboard focus into the palette search box",
                    Keyboard.FocusedElement,
                    palette));

                Assert.True(palette.FindName("PaletteOverlay") is Border { Visibility: Visibility.Visible });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteTabTraversalStaysInsideTheActiveModal(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);

                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var panel = Assert.IsType<Border>(palette.FindName("PalettePanel"));
                var searchBox = Assert.IsType<TextBox>(palette.FindName("SearchBox"));

                // Forward traversal from the entry field must stay on the modal panel.
                var forward = new List<FrameworkElement> { searchBox };
                FrameworkElement current = searchBox;

                for (var hop = 0; hop < TraversalHops; hop++)
                {
                    current = MoveFocusFrom(current, FocusNavigationDirection.Next);
                    Assert.NotNull(current);
                    forward.Add(current);
                    Assert.True(IsInside(panel, current), Failure(
                        $"Tab left the active palette panel after {hop + 1} forward traversal(s)",
                        current,
                        panel));
                }

                // Backward traversal from the same entry field must stay on the modal panel too.
                current = searchBox;

                for (var hop = 0; hop < TraversalHops; hop++)
                {
                    current = MoveFocusFrom(current, FocusNavigationDirection.Previous);
                    Assert.NotNull(current);
                    Assert.True(IsInside(panel, current), Failure(
                        $"Shift+Tab left the active palette panel after {hop + 1} backward traversal(s)",
                        current,
                        panel));
                }

                Assert.True(forward.Distinct().Count() > 1,
                    "Forward traversal inside the palette must actually move between palette controls.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteCapturesInvokerBeforeDeferredFocusWork(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        using var activity = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activity);
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);
            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                var other = Assert.IsType<Button>(view.FindName("OpenWorkflowConsoleButton"));
                FocusFromShell(invoker, "palette invoker before deferred work");
                shell.OpenCommandPaletteCommand.Execute(null);
                // Another handler can move focus before the queued palette entry runs.
                other.Focus();
                Assert.Same(other, Keyboard.FocusedElement);
                Settle(view);
                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                RaiseEscape(palette);
                Settle(view);
                Assert.Same(invoker, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteEscapeRestoresFocusToTheReachableInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);

                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var panel = Assert.IsType<Border>(palette.FindName("PalettePanel"));
                Assert.True(IsInside(panel, Keyboard.FocusedElement));

                RaiseEscape(palette);

                Settle(view);
                Assert.False(shell.CommandPalette.IsOpen);

                AssertReachable(invoker, "palette invoker after close");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "closing the palette with Escape must restore keyboard focus to the invoker that opened it",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void ActivityCenterOpenMovesFocusInsideAndCloseRestoresTheInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenActivityCenterButton"));
                Assert.Equal("Открыть Центр активности", AutomationProperties.GetName(invoker));
                FocusFromShell(invoker, "activity center invoker");

                shell.OpenActivityCenterCommand.Execute(null);
                Settle(view);

                var overlay = Assert.IsType<ActivityCenterView>(view.FindName("ActivityCenterOverlay"));
                var closeButton = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.Equal("Закрыть центр активности", AutomationProperties.GetName(closeButton));

                var focused = Keyboard.FocusedElement;
                Assert.True(ReferenceEquals(focused, closeButton) || IsInside(overlay, focused), Failure(
                    "opening the Activity Center must move keyboard focus inside the overlay",
                    focused,
                    overlay));

                closeButton.Command!.Execute(closeButton.CommandParameter);
                Settle(view);

                Assert.False(shell.IsActivityCenterOpen);
                AssertReachable(invoker, "activity center invoker after close");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "closing the Activity Center must restore keyboard focus to the invoker that opened it",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 1.0)]
    [InlineData(AppTheme.Dark, 1.5)]
    [InlineData(AppTheme.Dark, 2.0)]
    [InlineData(AppTheme.Light, 1.0)]
    [InlineData(AppTheme.Light, 1.5)]
    [InlineData(AppTheme.Light, 2.0)]
    public void OverlayFocusFlowHoldsAtEveryLayoutScale(AppTheme theme, double scale)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                view.LayoutTransform = new ScaleTransform(scale, scale);
                view.UpdateLayout();

                // Palette: open, traverse, Escape, restore.
                var paletteInvoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(paletteInvoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);

                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var panel = Assert.IsType<Border>(palette.FindName("PalettePanel"));
                Assert.True(IsInside(panel, Keyboard.FocusedElement), Failure(
                    $"palette entry focus at {theme} scale {scale:0.0}",
                    Keyboard.FocusedElement,
                    panel));

                var current = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);

                for (var hop = 0; hop < TraversalHops; hop++)
                {
                    current = MoveFocusFrom(current, FocusNavigationDirection.Next);
                    Assert.NotNull(current);
                    Assert.True(IsInside(panel, current), Failure(
                        $"Tab left the palette panel at {theme} scale {scale:0.0} after {hop + 1} hop(s)",
                        current,
                        panel));
                }

                RaiseEscape(palette);
                Settle(view);
                AssertReachable(paletteInvoker, "palette invoker at scaled layout");
                Assert.True(ReferenceEquals(paletteInvoker, Keyboard.FocusedElement), Failure(
                    $"palette close did not restore the invoker at {theme} scale {scale:0.0}",
                    Keyboard.FocusedElement,
                    paletteInvoker));

                // Evidence: the palette rendered with keyboard focus inside it.
                FocusFromShell(paletteInvoker, "command palette invoker");
                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);
                var evidencePalette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var evidencePanel = Assert.IsType<Border>(evidencePalette.FindName("PalettePanel"));
                Assert.True(IsInside(evidencePanel, Keyboard.FocusedElement), Failure(
                    $"palette evidence focus at {theme} scale {scale:0.0}",
                    Keyboard.FocusedElement,
                    evidencePanel));
                CaptureScreenshot(view, $"keyboardfocus_palette_focused_{theme}_{scale * 100:0}.png", 1280, 800);
                RaiseEscape(evidencePalette);
                Settle(view);

                // Activity Center: open, close, restore.
                var activityInvoker = Assert.IsType<Button>(view.FindName("OpenActivityCenterButton"));
                FocusFromShell(activityInvoker, "activity center invoker");

                shell.OpenActivityCenterCommand.Execute(null);
                Settle(view);

                var overlay = Assert.IsType<ActivityCenterView>(view.FindName("ActivityCenterOverlay"));
                var closeButton = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.True(
                    ReferenceEquals(Keyboard.FocusedElement, closeButton) || IsInside(overlay, Keyboard.FocusedElement),
                    Failure(
                        $"Activity Center entry focus at {theme} scale {scale:0.0}",
                        Keyboard.FocusedElement,
                        overlay));

                // Evidence: the Activity Center rendered with keyboard focus inside the overlay.
                CaptureScreenshot(view, $"keyboardfocus_activitycenter_focused_{theme}_{scale * 100:0}.png", 1280, 800);

                closeButton.Command!.Execute(closeButton.CommandParameter);
                Settle(view);
                AssertReachable(activityInvoker, "activity center invoker at scaled layout");
                Assert.True(ReferenceEquals(activityInvoker, Keyboard.FocusedElement), Failure(
                    $"Activity Center close did not restore the invoker at {theme} scale {scale:0.0}",
                    Keyboard.FocusedElement,
                    activityInvoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OrdinaryShellTabOrderStillReachesTheToolbarAndPanes()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var toggleLeft = Assert.IsType<Button>(view.FindName("ToggleLeftPaneButton"));
                FocusFromShell(toggleLeft, "left pane toggle");

                var reached = new HashSet<DependencyObject> { toggleLeft };
                FrameworkElement current = toggleLeft;

                for (var hop = 0; hop < 40; hop++)
                {
                    current = MoveFocusFrom(current, FocusNavigationDirection.Next);
                    reached.Add(current);
                }

                // Closing overlays must not have broken ordinary non-overlay traversal.
                Assert.Contains(reached, element => ReferenceEquals(element, view.FindName("OpenCommandPaletteButton")));
                Assert.Contains(reached, element => ReferenceEquals(element, view.FindName("OpenActivityCenterButton")));
                Assert.True(
                    reached.Any(element => element is TextBox or ComboBox or ListBox),
                    "Ordinary shell traversal must still reach editable and list controls outside the overlays.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Material transition defect: the palette's deferred focus work has no notion of which open it belongs
    /// to, so an open/close/open cycle inside one dispatcher pass lets a stale callback capture the palette
    /// itself as the invoker. This drives the whole cycle without pumping in between and only then asserts
    /// where keyboard focus really ended up.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteOpenCloseOpenWithoutPumpingRestoresTheRealInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "command palette invoker");

                // Deliberately no pumping between the transitions.
                shell.OpenCommandPaletteCommand.Execute(null);
                shell.CloseCommandPaletteCommand.Execute(null);
                shell.OpenCommandPaletteCommand.Execute(null);
                shell.CloseCommandPaletteCommand.Execute(null);

                Settle(view);
                Assert.False(shell.CommandPalette.IsOpen);

                AssertReachable(invoker, "palette invoker after an unpumped open/close/open cycle");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "an unpumped open/close/open cycle must leave keyboard focus on the real invoker",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The same transition with a single dispatcher pass between the reopen and the final close: the stale
    /// capture lands while the palette is already closed again, so the final close has no invoker to restore.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PaletteOpenCloseOpenWithOnePumpThenCloseRestoresTheRealInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);
                shell.CloseCommandPaletteCommand.Execute(null);
                shell.OpenCommandPaletteCommand.Execute(null);

                Settle(view);
                Assert.True(shell.CommandPalette.IsOpen);

                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var panel = Assert.IsType<Border>(palette.FindName("PalettePanel"));
                Assert.True(IsInside(panel, Keyboard.FocusedElement), Failure(
                    "the surviving open must own the palette keyboard focus",
                    Keyboard.FocusedElement,
                    panel));

                shell.CloseCommandPaletteCommand.Execute(null);
                Settle(view);

                AssertReachable(invoker, "palette invoker after the reopened palette closes");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "the surviving open's invoker must survive the final close",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Same two transition shapes for the Activity Center overlay, which has its own callback pair.</summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void ActivityCenterOpenCloseOpenTransitionsRestoreTheRealInvoker(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenActivityCenterButton"));
                FocusFromShell(invoker, "activity center invoker");

                // Shape 1: no pumping at all.
                shell.OpenActivityCenterCommand.Execute(null);
                shell.CloseActivityCenterCommand.Execute(null);
                shell.OpenActivityCenterCommand.Execute(null);
                Settle(view);

                var overlay = Assert.IsType<ActivityCenterView>(view.FindName("ActivityCenterOverlay"));
                var closeButton = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.True(shell.IsActivityCenterOpen);
                Assert.True(
                    ReferenceEquals(Keyboard.FocusedElement, closeButton) || IsInside(overlay, Keyboard.FocusedElement),
                    Failure("the surviving Activity Center open must own the focus", Keyboard.FocusedElement, overlay));

                // Shape 2: one pump between the reopen and the final close, preceded by a whole extra cycle.
                shell.CloseActivityCenterCommand.Execute(null);
                Settle(view);
                shell.OpenActivityCenterCommand.Execute(null);
                shell.CloseActivityCenterCommand.Execute(null);
                shell.OpenActivityCenterCommand.Execute(null);
                Settle(view);

                Assert.True(
                    ReferenceEquals(Keyboard.FocusedElement, closeButton) || IsInside(overlay, Keyboard.FocusedElement),
                    Failure("the last Activity Center open must own the focus", Keyboard.FocusedElement, overlay));

                shell.CloseActivityCenterCommand.Execute(null);
                Settle(view);

                AssertReachable(invoker, "activity center invoker after the reopened overlay closes");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "the surviving Activity Center open's invoker must survive the final close",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The palette's own <c>actions.activity-center</c> entry: the palette is closed and the overlay opens in
    /// the same synchronous turn, before the palette's deferred focus callback has ever run.
    /// </summary>
    [Fact]
    public void PaletteExecuteSelectedActivityCenterEntryFocusesTheOverlayAndRestoresAfterwards()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var invoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(invoker, "command palette invoker");

                shell.OpenCommandPaletteCommand.Execute(null);

                var item = Assert.Single(
                    shell.CommandPalette.Items.Where(candidate => candidate.Id == "actions.activity-center"));
                shell.CommandPalette.SearchQuery = item.Title;
                Assert.Same(item, shell.CommandPalette.SelectedItem);

                // No pump: the palette's own deferred focus callback has not run yet.
                shell.CommandPalette.ExecuteSelectedCommand.Execute(null);

                Settle(view);
                Assert.False(shell.CommandPalette.IsOpen);
                Assert.True(shell.IsActivityCenterOpen);

                var overlay = Assert.IsType<ActivityCenterView>(view.FindName("ActivityCenterOverlay"));
                var closeButton = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.True(
                    ReferenceEquals(Keyboard.FocusedElement, closeButton) || IsInside(overlay, Keyboard.FocusedElement),
                    Failure(
                        "executing the palette's Activity Center entry must move focus into the overlay",
                        Keyboard.FocusedElement,
                        overlay));

                closeButton.Command!.Execute(closeButton.CommandParameter);
                Settle(view);

                AssertReachable(invoker, "palette invoker after the palette-opened overlay closes");
                Assert.True(ReferenceEquals(invoker, Keyboard.FocusedElement), Failure(
                    "closing the palette-opened Activity Center must restore keyboard focus to the palette invoker",
                    Keyboard.FocusedElement,
                    invoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The retained shell view is unloaded and reloaded with the same data context. Subscription handling has
    /// to survive that, or the overlay focus flow silently dies for the rest of the session.
    /// </summary>
    [Fact]
    public void OverlayFocusFlowSurvivesUnloadAndReloadOfTheRetainedView()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        using var activityCenter = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activityCenter);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                window.Content = null;
                Pump();
                window.Content = view;
                Settle(view);

                // Palette entry focus after the reload.
                var paletteInvoker = Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton"));
                FocusFromShell(paletteInvoker, "command palette invoker after reload");
                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);

                var panel = Assert.IsType<Border>(
                    FindVisualDescendants<CommandPaletteView>(view).Single().FindName("PalettePanel"));
                Assert.True(IsInside(panel, Keyboard.FocusedElement), Failure(
                    "palette entry focus must work again after the view is reloaded",
                    Keyboard.FocusedElement,
                    panel));

                shell.CloseCommandPaletteCommand.Execute(null);
                Settle(view);
                Assert.True(ReferenceEquals(paletteInvoker, Keyboard.FocusedElement), Failure(
                    "palette focus restoration must work again after the view is reloaded",
                    Keyboard.FocusedElement,
                    paletteInvoker));

                // Activity Center entry focus after the same reload.
                var activityInvoker = Assert.IsType<Button>(view.FindName("OpenActivityCenterButton"));
                FocusFromShell(activityInvoker, "activity center invoker after reload");
                shell.OpenActivityCenterCommand.Execute(null);
                Settle(view);

                var overlay = Assert.IsType<ActivityCenterView>(view.FindName("ActivityCenterOverlay"));
                var closeButton = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.True(
                    ReferenceEquals(Keyboard.FocusedElement, closeButton) || IsInside(overlay, Keyboard.FocusedElement),
                    Failure(
                        "Activity Center entry focus must work again after the view is reloaded",
                        Keyboard.FocusedElement,
                        overlay));

                closeButton.Command!.Execute(closeButton.CommandParameter);
                Settle(view);
                Assert.True(ReferenceEquals(activityInvoker, Keyboard.FocusedElement), Failure(
                    "Activity Center focus restoration must work again after the view is reloaded",
                    Keyboard.FocusedElement,
                    activityInvoker));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string Failure(string reason, IInputElement? focused, DependencyObject expectedScope) =>
        $"{reason}. FocusedElement={Describe(focused)}; IsInside({expectedScope.GetType().Name})={IsInside(expectedScope, focused)}; IsVisible={(focused as UIElement)?.IsVisible}; " +
        $"ActiveWindow={(focused as FrameworkElement)?.IsKeyboardFocusWithin}";

    private static string Describe(IInputElement? focused) =>
        focused is null
            ? "<null>"
            : $"{(focused as FrameworkElement)?.Name ?? "unnamed"}:{focused.GetType().FullName}:'{AutomationProperties.GetName(focused as DependencyObject)}'";

    private static void AssertReachable(UIElement element, string because)
    {
        Assert.True(element.IsVisible && element.IsEnabled && element.Focusable,
            $"Invoker must remain reachable: {because}. IsVisible={element.IsVisible}, IsEnabled={element.IsEnabled}, IsFocusable={element.Focusable}");
    }

    /// <summary>Focuses the invoker and asserts WPF really reports it, so later failures are about the flow.</summary>
    private static void FocusFromShell(FrameworkElement element, string because)
    {
        Settle(null);
        element.Focus();
        Pump();

        Assert.True(ReferenceEquals(element, Keyboard.FocusedElement),
            $"Could not establish keyboard focus on the {because}: FocusedElement={Describe(Keyboard.FocusedElement)}. " +
            "The window must be shown and activated for keyboard focus to exist at all.");
    }

    [Theory]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Light, true)]
    public void PaletteControlTabTraversalStaysInsideTheActiveModal(AppTheme theme, bool backwards)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        using var activity = CreateActivityCenterViewModel();
        var shell = CreateShell(provider, activity);
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);
            try
            {
                FocusFromShell(Assert.IsType<Button>(view.FindName("OpenCommandPaletteButton")), "palette invoker");
                shell.OpenCommandPaletteCommand.Execute(null);
                Settle(view);
                var palette = FindVisualDescendants<CommandPaletteView>(view).Single();
                var panel = Assert.IsType<Border>(palette.FindName("PalettePanel"));
                DependencyObject current = Assert.IsType<TextBox>(palette.FindName("SearchBox"));

                // Exercise WPF's actual Tab navigation with explicit modifiers, without changing
                // the user's physical keyboard state. MoveFocus otherwise reads Keyboard.Modifiers.
                var flags = System.Reflection.BindingFlags.NonPublic;
                var navigator = typeof(KeyboardNavigation).GetProperty("Current", flags | System.Reflection.BindingFlags.Static)!.GetValue(null);
                var navigate = typeof(KeyboardNavigation).GetMethod("Navigate", flags | System.Reflection.BindingFlags.Instance,
                    null, [typeof(DependencyObject), typeof(Key), typeof(ModifierKeys), typeof(bool)], null);
                Assert.NotNull(navigator);
                Assert.NotNull(navigate);
                var modifiers = ModifierKeys.Control | (backwards ? ModifierKeys.Shift : ModifierKeys.None);
                for (var hop = 0; hop < TraversalHops; hop++)
                {
                    navigate.Invoke(navigator, [current, Key.Tab, modifiers, true]);
                    Pump();
                    current = Assert.IsAssignableFrom<DependencyObject>(Keyboard.FocusedElement);
                    Assert.True(IsInside(panel, Keyboard.FocusedElement), Failure(
                        $"{modifiers}+Tab left palette after {hop + 1} traversals", Keyboard.FocusedElement, panel));
                }
            }
            finally { window.Close(); }
        });
    }

    private static FrameworkElement MoveFocusFrom(FrameworkElement from, FocusNavigationDirection direction)
    {
        var window = Window.GetWindow(from);
        var moved = from.MoveFocus(new TraversalRequest(direction));
        Pump();

        // Foreground ownership is diagnostic only: Windows can deny activation
        // while the owned WPF window still has valid keyboard focus. Assert the
        // actual traversal result, without restoring focus or retrying the step.
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var foregroundProcess);
        var focused = Keyboard.FocusedElement;
        Assert.True(focused is FrameworkElement,
            $"Traversal lost keyboard focus: direction={direction}, moved={moved}, from={Describe(from)}, "
            + $"windowActive={window?.IsActive}, foreground PID={foregroundProcess}, test PID={Environment.ProcessId}. "
            + "The step is not retried after dispatcher processing.");
        return Assert.IsAssignableFrom<FrameworkElement>(focused);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>
    /// Runs the exact handler the physical Escape key raises: the palette declares Escape as a KeyBinding,
    /// so the bound command is invoked instead of pretending a key was injected.
    /// </summary>
    private static void RaiseEscape(CommandPaletteView palette)
    {
        var bindings = palette.InputBindings.OfType<KeyBinding>().Concat(
            ((TextBox)palette.FindName("SearchBox")).InputBindings.OfType<KeyBinding>());

        var escape = bindings.FirstOrDefault(binding => binding.Key == Key.Escape);

        Assert.NotNull(escape);
        Assert.True(escape.Command.CanExecute(null), "The declared Escape binding must be executable while the palette is open.");
        escape.Command.Execute(null);
        Pump();
    }

    private static void Settle(FrameworkElement? view)
    {
        view?.UpdateLayout();
        Pump();
        view?.UpdateLayout();
        Pump();
    }

    private static void Pump()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;

        // The shipped views focus asynchronously (Dispatcher.BeginInvoke on open), so drain everything
        // queued at or above Background before asserting.
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    private static bool IsInside(DependencyObject scope, IInputElement? element)
    {
        if (element is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (ReferenceEquals(current, scope))
            {
                return true;
            }

            if (current is System.Windows.Media.Media3D.Visual3D)
            {
                current = VisualTreeHelper.GetParent(current);
                continue;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static UnifiedWorkspaceShellViewModel CreateShell(
        ServiceProvider provider,
        ActivityCenterViewModel activityCenter) =>
        new(
            provider.GetRequiredService<MainWindowViewModel>(),
            new StubLayoutPersistenceService(),
            activityCenter);

    private static ActivityCenterViewModel CreateActivityCenterViewModel()
    {
        var service = new ActivityCenterService(new SensitiveDataFilter().Redact, new FixedTimeProvider(Now));

        service.AppendRange(new[]
        {
            new ActivityEvent(
                "focus-event-1",
                Now.AddMinutes(-5),
                ActivityEventKind.Execution,
                ActivityRoleNames.Coder,
                ActivityEventState.Completed,
                ActivityEventSource.Native,
                "Focus acceptance event",
                "Rendered inside the unified workspace shell overlay while the overlay holds keyboard focus."),
            new ActivityEvent(
                "focus-event-2",
                Now.AddMinutes(-2),
                ActivityEventKind.Health,
                ActivityRoleNames.System,
                ActivityEventState.Running,
                ActivityEventSource.Synthetic,
                "Synthetic focus acceptance event",
                "In-memory fixture; no native model or provider call is made.")
        });

        return new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new SilentClipboard());
    }

    private static (Window Window, UnifiedWorkspaceShellView View) OpenShell(
        UnifiedWorkspaceShellViewModel viewModel,
        double width,
        double height)
    {
        var view = new UnifiedWorkspaceShellView { DataContext = viewModel };

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,

            // Focus traversal is the subject here, so the window must be a real, activated input window.
            ShowActivated = true,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Show();
        window.Activate();
        window.Focus();

        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Pump();

        return (window, view);
    }

    private static void CaptureScreenshot(FrameworkElement root, string fileName, int pixelWidth, int pixelHeight)
    {
        var renderBitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        ScreenshotFile.Save(encoder, System.IO.Path.Combine(ScreenshotFile.OutputDirectory, fileName));
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < childCount; index++)
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

// Dispatcher pumping can run another test's queued Invoke while a focus assertion is pending.
// These tests also share Application.Resources and the desktop's active input window.
// Keep them exclusive without weakening the actual Keyboard.FocusedElement assertions.
[CollectionDefinition("Keyboard focus isolation", DisableParallelization = true)]
public sealed class KeyboardFocusIsolationCollection
{
}
