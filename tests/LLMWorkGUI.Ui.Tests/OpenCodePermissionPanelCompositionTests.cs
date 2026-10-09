using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>Shown shipped WPF permission panel; production SQLite/locks, explicitly synthetic native lifecycle.</summary>
[Trait("Category", "VisualUi")]
public sealed class OpenCodePermissionPanelCompositionTests
{
    [Theory]
    [InlineData(AppTheme.Dark, 1.0, 1060)]
    [InlineData(AppTheme.Light, 1.0, 1060)]
    [InlineData(AppTheme.Dark, 1.5, 720)]
    [InlineData(AppTheme.Light, 2.0, 720)]
    public void ShippedPermissionPanel_IsVisibleAndDenyCommandCompletesOwnedExecution(AppTheme theme, double scale, int width)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var directory = Directory.CreateTempSubdirectory("llm-permission-panel-");
            var path = directory.FullName;
            var root = Directory.CreateDirectory(Path.Combine(path, "workspace")).FullName;
            var services = new ServiceCollection(); var native = new SyntheticLifecycle();
            services.AddSingleton<IOpenCodeSessionLifecycleService>(native);
            services.AddSingleton(new FakeOpenCodeProcessConnection().Connection);
            services.AddApplication(); services.AddInfrastructure(path); services.AddAppUi();
            var provider = services.BuildServiceProvider(); var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
            Window? window = null;
            WorkspaceViewModel? vm = null;
            try
            {
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync()); Seed(factory, root);
                vm = provider.GetRequiredService<WorkspaceViewModel>(); vm.ProjectId = "project-1";
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm };
                window = new Window { Content = host, Width = width, Height = 800, ShowInTaskbar = false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window); Click(host, "Обновить маршруты"); WaitIdle(vm, window);
                vm.SelectedOpenCodeRoute = Assert.Single(vm.OpenCodeRoutes); Settle(window);
                Click(host, "Создать сессию"); WaitIdle(vm, window);
                Descendants<TextBox>(host).Single(t => t.Name == "MessageComposer").Text = "Синтетическая проверка панели разрешений";
                Settle(window); Click(host, "Отправить ↑");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!vm.HasPendingOpenCodePermissions && DateTime.UtcNow < deadline) Settle(window);
                Assert.True(vm.HasPendingOpenCodePermissions); Settle(window);
                Assert.Equal("WaitingApproval", Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal("1", Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
                Assert.Empty(native.Replies);
                var allow = Descendants<Button>(host).Single(b => ReferenceEquals(b.DataContext, vm) && Equals(b.Content, "Разрешить один раз"));
                var deny = Descendants<Button>(host).Single(b => ReferenceEquals(b.DataContext, vm) && Equals(b.Content, "Отклонить"));
                var execution = Descendants<Button>(host).Single(b => ReferenceEquals(b.DataContext, vm) && Equals(b.Content, "На выполнение"));
                Assert.True(allow.IsVisible); Assert.True(allow.IsEnabled); Assert.True(deny.IsEnabled); Assert.False(execution.IsEnabled);
                var request = Descendants<TextBlock>(host).Single(t => t.Text == native.Pending[0].OriginalRequestDisplay);
                Assert.True(request.IsVisible); Assert.True(request.ActualHeight > 0);
                Assert.Equal(((SolidColorBrush)request.FindResource("Theme.Text.Primary")).Color, ((SolidColorBrush)request.Foreground).Color);
                Assert.True(deny.TransformToAncestor(host).Transform(new Point(0, 0)).Y + deny.ActualHeight <= host.ActualHeight);
                Capture(host, $"opencode_permission_{theme}_{scale * 100:0}_{width}.png", scale);
                Click(host, "Отклонить"); WaitIdle(vm, window);
                Assert.Equal(("native-owned", "fixture-receipt", "reject"), Assert.Single(native.Replies));
                Assert.False(vm.HasPendingOpenCodePermissions);
                Assert.Equal("Succeeded", Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal("Idle", Scalar(factory, "SELECT State FROM Sessions"));
                Assert.Equal("0", Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
            finally
            {
                native.Stop();
                if (vm is not null && window is not null) WaitIdle(vm, window);
                window?.Close(); Pump(provider.DisposeAsync().AsTask());
                using (var connection = factory.CreateConnection()) SqliteConnection.ClearPool(connection);
                var target = Path.GetFullPath(directory.FullName);
                if (!target.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup target");
                directory.Delete(true);
            }
        });
    }
    private sealed class SyntheticLifecycle : IOpenCodeSessionLifecycleService
    {
        private readonly TaskCompletionSource<TurnResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<OpenCodePendingPermission> Pending { get; private set; } = Array.Empty<OpenCodePendingPermission>();
        public List<(string Session, string Receipt, string Reply)> Replies { get; } = new();
        public Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new OpenCodeSessionResponse { Id = "native-owned" });
        public Task<OpenCodeSessionResponse> ContinueSessionAsync(string sessionId, SessionBinding binding, CancellationToken cancellationToken = default) => Task.FromResult(new OpenCodeSessionResponse { Id = sessionId });
        public Task<TurnResult> ExecuteTurnAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken = default)
        {
            Pending = new[] { new OpenCodePendingPermission { ReceiptId = "fixture-receipt", RequestId = "per_fixture", SessionId = sessionId,
                NativeKind = "edit", Kind = LLMWorkGUI.Domain.Enums.NormalizedApprovalKind.WriteFile,
                Explanation = "Запись файла. Разрешение действует для одного исходного запроса.",
                OriginalRequestDisplay = "Синтетический запрос записи: probe.txt. Исходные данные после очистки секретов.", CanAllowOnce = true, CanDeny = true } };
            return _completion.Task;
        }
        public IReadOnlyList<OpenCodePendingPermission> GetPendingPermissions(string sessionId) => Pending;
        public Task<bool> ReplyPermissionAsync(string sessionId, string receiptId, string response, CancellationToken cancellationToken = default)
        { Replies.Add((sessionId, receiptId, response)); Pending = Array.Empty<OpenCodePendingPermission>(); _completion.SetResult(new() { SessionId = sessionId, Status = TurnResult.CompletedStatus, OutputText = "Синтетический запрос отклонён." }); return Task.FromResult(true); }
        public void Stop()
        {
            Pending = Array.Empty<OpenCodePendingPermission>();
            _completion.TrySetResult(new() { SessionId = "native-owned", Status = TurnResult.CancelledStatus, OutputText = string.Empty });
        }
        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default)
        { Stop(); return Task.FromResult(true); }
        public Task<OpenCodeSessionResponse> ResetSessionAsync(string oldSessionId, OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<string> GetAncestry(string sessionId) => Array.Empty<string>();
    }
    private static void Seed(ISqliteConnectionFactory factory, string root)
    {
        using var connection = Pump(factory.OpenConnectionAsync()); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('profile-1','Синтетический профиль','OpenCode','PrivateSource',1,'2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('account-1','profile-1','Синтетический аккаунт','Valid','Healthy','2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            INSERT INTO Models (Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,Health,DiscoveredAtUtc)
                VALUES ('model-1','OpenCode','profile-1','provider/native-model','Синтетическая модель','Supported','UserDefined',1,'Healthy','2026-10-02T00:00:00Z');
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('route-1','OpenCode','profile-1','account-1','model-1','PrivateSource',1,'Healthy','2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            INSERT INTO Projects (Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('project-1','Тестовый проект',$root,'PrivateSource','2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery();
    }
    private static string? Scalar(ISqliteConnectionFactory factory, string sql)
    {
        using var connection = Pump(factory.OpenConnectionAsync()); using var command = connection.CreateCommand(); command.CommandText = sql;
        var value = command.ExecuteScalar(); return value is null or DBNull ? null : Convert.ToString(value);
    }
    private static void Execute(ISqliteConnectionFactory factory, string sql)
    { using var connection = Pump(factory.OpenConnectionAsync()); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void Click(DependencyObject host, string text)
    { var button = Descendants<Button>(host).Single(b => b.DataContext is WorkspaceViewModel && Equals(b.Content, text)); Assert.True(button.IsEnabled); button.Command.Execute(button.CommandParameter); }
    private static void WaitIdle(WorkspaceViewModel vm, Window window)
    { var until = DateTime.UtcNow.AddSeconds(10); while (vm.IsBusy && DateTime.UtcNow < until) Settle(window); Assert.False(vm.IsBusy); Settle(window); }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        task = task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!task.IsCompleted)
        { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static void Capture(FrameworkElement root, string filename, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, filename));
    }
}
