using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("UiInspector visual isolation")]
public sealed class NativeBackendLayoutTests
{
    [Theory]
    [InlineData(false, 650)]
    [InlineData(false, 420)]
    [InlineData(true, 650)]
    [InlineData(true, 420)]
    public void NativePanelActionsAndStatusStayInsideTheAvailableWidth(bool mirasim, double width)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var lifecycle = new FakeCursorAcpSessionLifecycleService
            {
                StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
                SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence
                {
                    SessionId = "long-observed-native-session-" + new string('x', 180)
                })
            };
            object model = mirasim
                ? provider.GetRequiredService<MirasimWorkspaceViewModel>()
                : new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy());
            if (model is CursorWorkspaceViewModel cursor)
            {
                AwaitDeterministic(cursor.StartBackendAsync());
                AwaitDeterministic(cursor.CreateSessionAsync(Path.GetTempPath()));
            }
            var host = new ContentControl { Content = model };
            var scroll = new ScrollViewer
            {
                Content = host,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var window = new Window
            {
                Content = scroll, Width = width, Height = 900,
                WindowStyle = WindowStyle.None, ShowActivated = false
            };
            foreach (var path in new[] { "Themes/Shared.xaml", "Views/ScreenTemplates.xaml" })
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/" + path)
                });
            try
            {
                window.Show();
                window.UpdateLayout();
                var actions = Descendants<Button>(host).Where(x => x.IsVisible).ToArray();
                Assert.Contains(actions, x => Equals(x.Content, "Отправить запрос"));
                Assert.Contains(actions, x => Equals(x.Content, "Отменить шаг"));
                if (mirasim) Assert.Contains(actions, x => Equals(x.Content, "Согласовать"));
                foreach (var element in actions.Cast<FrameworkElement>()
                             .Concat(Descendants<TextBlock>(host).Where(x => x.IsVisible)))
                {
                    var bounds = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= host.ActualWidth + 1,
                        $"{element.GetType().Name} '{(element is Button b ? b.Content : ((TextBlock)element).Text)}' " +
                        $"extends to {bounds.Right:F1} beyond panel width {host.ActualWidth:F1}.");
                }
                var prompt = Descendants<TextBox>(host).Single(x => x.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "PromptInput");
                Assert.True(prompt.ActualWidth >= 100, "The responsive composer must retain an usable text input.");
                var boundsToCapture = new Rect(new Point(), scroll.RenderSize);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(scroll)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute, Viewbox = boundsToCapture,
                        ViewportUnits = BrushMappingMode.Absolute, Viewport = boundsToCapture
                    }, null, boundsToCapture);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(scroll.ActualWidth),
                    (int)Math.Ceiling(scroll.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory,
                    $"native_backend_{(mirasim ? "mirasim" : "cursor")}_{width}.png"));
            }
            finally { window.Close(); }
        });
    }

    private static void AwaitDeterministic(System.Threading.Tasks.Task task)
    {
        Assert.True(task.IsCompleted, "The lifecycle fixture must complete without dispatcher waits.");
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
