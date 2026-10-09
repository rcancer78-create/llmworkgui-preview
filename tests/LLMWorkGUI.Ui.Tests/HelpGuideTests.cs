using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMWorkGUI.App.Help;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("UiInspector visual isolation")]
public sealed class HelpGuideTests
{
    [Fact]
    public void PackagedGuideContainsTheCompleteCanonicalDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "docs", "USER_ADMIN_GUIDE.md"));
        Assert.Equal(source, HelpGuideContent.Load());
    }

    [Fact]
    public void SearchUsesVisibleTextAcrossStylesAndCodeStaysInert()
    {
        StaTestRunner.Run(() =>
        {
            var guide = HelpMarkdownRenderer.Render("""
                # Руководство
                Найдите **резервную** `копию` проекта.

                ```powershell
                ## Это команда, а не раздел
                <Button Click="Execute" />
                ```
                ## Восстановление
                [Ссылка](https://example.invalid) и **незакрытый маркер
                """);
            Assert.Equal(new[] { "Руководство", "Восстановление" }, guide.Headings.Select(x => x.Title));
            var match = Assert.Single(guide.Search.Find("РЕЗЕРВНУЮ копию"));
            Assert.Equal("резервную копию", new TextRange(match.Start, match.End).Text);
            Assert.Empty(guide.Search.Find("  "));
            Assert.Contains("## Это команда, а не раздел", guide.Search.Text);
            Assert.Contains("<Button Click=\"Execute\" />", guide.Search.Text);
            Assert.Contains("**незакрытый маркер", guide.Search.Text);
            Assert.All(guide.Document.Blocks.Cast<Paragraph>().SelectMany(p => p.Inlines.Cast<Inline>()),
                inline => Assert.IsType<Run>(inline));
        });
    }

    [Fact]
    public void SearchCyclesMatchesAndClearsStaleSelection()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var window = new HelpGuideWindow("# Поиск\n\nКопия проекта.\n\nПроверенная **копия** данных.");
            try
            {
                window.Show();
                var search = Assert.IsType<TextBox>(window.FindName("SearchBox"));
                var reader = Assert.IsType<RichTextBox>(window.FindName("GuideReader"));
                var status = Assert.IsType<TextBlock>(window.FindName("SearchStatus"));
                var next = Assert.IsType<Button>(window.FindName("NextMatchButton"));
                var previous = Assert.IsType<Button>(window.FindName("PreviousMatchButton"));
                search.Text = "копия";
                Assert.Equal("Совпадение 1 из 2", status.Text);
                Assert.Equal("Копия", reader.Selection.Text);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Совпадение 2 из 2", status.Text);
                Assert.Equal("копия", reader.Selection.Text);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Совпадение 1 из 2", status.Text);
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Совпадение 2 из 2", status.Text);
                search.Text = "несуществующая фраза";
                Assert.Equal("Совпадений нет.", status.Text);
                Assert.True(reader.Selection.IsEmpty);
                Assert.False(next.IsEnabled);
                search.Text = string.Empty;
                Assert.True(reader.Selection.IsEmpty);
                Assert.False(previous.IsEnabled);
                Assert.True(reader.IsReadOnly);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelpCommandAndF1ReuseOneOwnedWindowAndCloseWithOwner(bool unified)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var shell = unified ? new UnifiedWorkspaceShellViewModel(vm, provider.GetRequiredService<ILayoutPersistenceService>()) : null;
            var owner = new MainWindow(vm, shell);
            try
            {
                owner.Show();
                owner.Activate();
                owner.Focus();
                CommandManager.InvalidateRequerySuggested();
                owner.UpdateLayout();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (unified)
                {
                    var bitmap = CaptureWholeWindow(owner, 96);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, "help_entry_unified.png"));
                }
                var binding = Assert.Single(owner.InputBindings.OfType<KeyBinding>(), key => key.Key == Key.F1);
                Assert.Same(HelpCommands.OpenGuide, binding.Command);
                var helpButton = unified
                    ? Assert.IsType<Button>(Assert.IsType<UnifiedWorkspaceShellView>(owner.FindName("UnifiedWorkspaceShell")).FindName("OpenHelpGuideButton"))
                    : Assert.IsType<Button>(owner.FindName("OpenHelpGuideButton"));
                Assert.True(helpButton.IsEnabled, "The visible help button must be usable in the shipped window.");
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(helpButton).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var first = Assert.Single(owner.OwnedWindows.OfType<HelpGuideWindow>());
                HelpCommands.OpenGuide.Execute(null, owner);
                Assert.Same(first, Assert.Single(owner.OwnedWindows.OfType<HelpGuideWindow>()));
                first.Close();
                HelpCommands.OpenGuide.Execute(null, owner);
                var reopened = Assert.Single(owner.OwnedWindows.OfType<HelpGuideWindow>());
                Assert.NotSame(first, reopened);
                owner.Close();
                Assert.False(reopened.IsVisible);
                Assert.DoesNotContain(System.Windows.Application.Current.Windows.OfType<Window>(), w => w is HelpGuideWindow);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void SearchRevealsTheMatchedLineInsideALongCodeBlock()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var code = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"command-{i}"));
            var window = new HelpGuideWindow("# Команды\n\n```\n" + code + "\nunique-last-line\n```")
                { Width = 800, Height = 480 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var reader = Assert.IsType<RichTextBox>(window.FindName("GuideReader"));
                Assert.IsType<TextBox>(window.FindName("SearchBox")).Text = "unique-last-line";
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("unique-last-line", reader.Selection.Text);
                var position = reader.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
                Assert.True(position.Top >= 0 && position.Bottom <= reader.ViewportHeight,
                    $"Selected result must be visible: {position}, viewport {reader.ViewportHeight}, offset {reader.VerticalOffset}.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EnterRevisitsTheSelectedHeadingAfterManualScrolling()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var window = new HelpGuideWindow("# Начало\n\n" +
                string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Абзац {i}.")) + "\n\n## Конец")
                { Width = 800, Height = 480 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var contents = Assert.IsType<ListBox>(window.FindName("ContentsList"));
                var reader = Assert.IsType<RichTextBox>(window.FindName("GuideReader"));
                contents.SelectedIndex = 0;
                reader.ScrollToEnd();
                window.UpdateLayout();
                Assert.True(reader.VerticalOffset > 0);
                contents.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                window.UpdateLayout();
                Assert.True(reader.VerticalOffset < reader.ViewportHeight,
                    "Activating the already selected first heading must return to the beginning.");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 1040, 760, 96)]
    [InlineData(AppTheme.Light, 560, 360, 192)]
    public void ActualGuideRendersAndNavigatesToItsLastSection(AppTheme theme, double width, double height, double dpi)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var window = new HelpGuideWindow { Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var contents = Assert.IsType<ListBox>(window.FindName("ContentsList"));
                var reader = Assert.IsType<RichTextBox>(window.FindName("GuideReader"));
                Assert.NotEmpty(contents.Items);
                contents.SelectedIndex = contents.Items.Count - 1;
                window.UpdateLayout();
                Assert.True(reader.VerticalOffset > 0, "The last chapter must be reachable through the table of contents.");
                contents.SelectedIndex = 0;
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                foreach (var name in new[] { "SearchBox", "PreviousMatchButton", "NextMatchButton", "CloseGuideButton", "ContentsList", "GuideReader" })
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1, name);
                }
                var bitmap = CaptureWholeWindow(window, dpi);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, $"help_guide_{theme}_{width}_{dpi}.png"));
            }
            finally { window.Close(); }
        });
    }
    // Capture the whole window visual: its arranged content offset and margins stay
    // inside the bitmap instead of cutting the right/bottom edges of the help root.
    private static RenderTargetBitmap CaptureWholeWindow(Window window, double dpi)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96),
            (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

}
