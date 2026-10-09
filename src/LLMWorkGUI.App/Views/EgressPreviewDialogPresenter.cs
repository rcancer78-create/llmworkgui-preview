using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.App.Views;

/// <summary>Reusable fragment consent dialog, opened by the existing composer.</summary>
public sealed class EgressPreviewDialogPresenter : IEgressPreviewPresenter
{
    public Task<IReadOnlyList<string>?> ConfirmAsync(EgressPreview preview, string destinationDisplay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = CreateWindow(preview, destinationDisplay, cancellationToken, out var confirmed);
        window.Owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner ? owner : null;
        window.ShowDialog();
        return Task.FromResult(confirmed());
    }

    private static Window CreateWindow(EgressPreview preview, string destinationDisplay, CancellationToken token,
        out Func<IReadOnlyList<string>?> confirmed)
    {
        var window = new Window { Title = "Подтверждение передачи текста", Width = 820, Height = 700,
            MinWidth = 500, MinHeight = 400, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        var layout = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var send = new Button { Content = "Передать подтверждённый текст", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        AutomationProperties.SetName(send, "Подтвердить передачу всех фрагментов");
        buttons.Children.Add(send); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var content = new StackPanel();
        void Label(string text)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary"); content.Children.Add(label);
        }
        Label("Проверьте каждый фрагмент. Отмеченные тексты будут переданы выбранному провайдеру. Изменение задания или маршрута потребует нового подтверждения.");
        Label("Выбранный получатель: " + destinationDisplay);
        Label($"Маршрут: {preview.Target.RouteId}\nАккаунт: {preview.Target.Binding.AccountId}\nМодель: {preview.Target.Binding.ModelId}\nСогласие действует до {preview.ExpiresAtUtc.ToLocalTime():HH:mm:ss}.");
        var checks = new List<CheckBox>();
        bool accepted = false;
        void Update() => send.IsEnabled = !token.IsCancellationRequested && DateTimeOffset.UtcNow < preview.ExpiresAtUtc
            && checks.Count == preview.Fragments.Count && checks.All(c => c.IsChecked == true);
        foreach (var fragment in preview.Fragments)
        {
            Label(fragment.Label);
            content.Children.Add(new TextBox { Text = fragment.Content, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 60, MaxHeight = 240, Margin = new Thickness(0, 0, 0, 4) });
            Label($"SHA-256: {fragment.ContentSha256}");
            var check = new CheckBox { Content = "Разрешаю передачу этого фрагмента", Tag = fragment.Id, Margin = new Thickness(0, 0, 0, 16) };
            check.SetResourceReference(Control.ForegroundProperty, "Theme.Text.Primary");
            AutomationProperties.SetName(check, "Разрешить фрагмент " + fragment.Id);
            check.Checked += (_, _) => Update(); check.Unchecked += (_, _) => Update();
            checks.Add(check); content.Children.Add(check);
        }
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = layout;
        send.Click += (_, _) => { Update(); if (!send.IsEnabled) return; accepted = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal,
            (_, _) => { Update(); if (token.IsCancellationRequested || DateTimeOffset.UtcNow >= preview.ExpiresAtUtc) window.Close(); }, window.Dispatcher);
        var registration = token.Register(() => window.Dispatcher.BeginInvoke(new Action(() => window.Close())));
        window.Closed += (_, _) => { timer.Stop(); registration.Dispose(); };
        confirmed = () => accepted && !token.IsCancellationRequested && DateTimeOffset.UtcNow < preview.ExpiresAtUtc
            ? checks.Select(c => (string)c.Tag).ToArray() : null;
        return window;
    }
}
