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

/// <summary>Shipped WPF commands, production DI/SQLite/checkout locks; native lifecycle is explicitly synthetic.</summary>
[Trait("Category", "VisualUi")]
public sealed class OpenCodeWorkspaceJournalCompositionTests
{
    [Theory]
    [InlineData(AppTheme.Dark, false, 1)]
    [InlineData(AppTheme.Light, false, 1)]
    [InlineData(AppTheme.Dark, true, 2)]
    [InlineData(AppTheme.Light, true, 1)]
    public void ShippedWorkspace_PersistsAdmissionAndHonorsTerminalOwnership(AppTheme theme, bool uncertain, int scale)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-opencode-journal-ui-");
            var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "workspace")).FullName;
            var services = new ServiceCollection();
            var native = new SyntheticLifecycle();
            services.AddSingleton<IOpenCodeSessionLifecycleService>(native);
            services.AddSingleton(new FakeOpenCodeProcessConnection().Connection);
            services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
            Window? window = null;
            try
            {
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                Seed(factory, root);
                var vm = provider.GetRequiredService<WorkspaceViewModel>();
                vm.ProjectId = "project-1";
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm };
                window = new Window { Content = host, Width = 1060, Height = 800, ShowInTaskbar = false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                Click(host, "Обновить маршруты"); WaitIdle(vm, window);
                var route = Assert.Single(vm.OpenCodeRoutes);
                Assert.Equal("route-1", route.Id);
                Descendants<ComboBox>(host).Single(c => ReferenceEquals(c.ItemsSource, vm.OpenCodeRoutes)).SelectedItem = route;
                Settle(window);
                Assert.Equal(route, vm.SelectedOpenCodeRoute);
                Click(host, "Создать сессию"); WaitIdle(vm, window);
                Assert.True(vm.IsSessionConfirmed, vm.SendBlocker);
                Assert.NotEqual(vm.LocalSessionId, vm.NativeSessionId);
                Assert.Equal(vm.LocalSessionId, Scalar(factory, "SELECT Id FROM Sessions"));
                Assert.Equal("0", Scalar(factory, "SELECT COUNT(*) FROM Executions"));
                native.OnExecute = (sessionId, request) =>
                {
                    Assert.Equal(vm.NativeSessionId, sessionId);
                    Assert.Equal("provider/native-model", request.Model);
                    Assert.Equal(vm.LocalSessionId, Scalar(factory, "SELECT SessionId FROM Executions"));
                    Assert.Equal("Running", Scalar(factory, "SELECT State FROM Executions"));
                    Assert.Equal("3", Scalar(factory, "SELECT COUNT(*) FROM ExecutionEvents"));
                    Assert.Equal(Scalar(factory, "SELECT Id FROM Executions"), Scalar(factory, "SELECT ExecutionId FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
                    Assert.Equal("route-1", Scalar(factory, "SELECT RequestedRouteId FROM ClientRequests"));
                    Assert.NotEqual(request.Prompt, Scalar(factory, "SELECT PromptHash FROM ClientRequests"));
                    return new TurnResult { SessionId = sessionId, Status = uncertain ? TurnResult.FailedStatus : TurnResult.CompletedStatus,
                        OutputText = "Синтетический ответ: проверка локального журнала.", IsDeliveryUncertain = uncertain };
                };
                Descendants<TextBox>(host).Single(t => t.Name == "MessageComposer").Text = "Синтетическая проверка журнала";
                Settle(window); Click(host, "Отправить ↑"); WaitIdle(vm, window);
                Assert.Equal(1, native.Dispatches);
                Assert.Equal("4", Scalar(factory, "SELECT COUNT(*) FROM ExecutionEvents"));
                Assert.Equal(uncertain ? "Ambiguous" : "Succeeded", Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal(uncertain ? "Ambiguous" : "Idle", Scalar(factory, "SELECT State FROM Sessions"));
                Assert.Equal(uncertain ? "1" : "0", Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
                Assert.Equal(uncertain, vm.IsWriterLockRetained);
                Capture(host, $"opencode_journal_{theme}_{(uncertain ? "ambiguous" : "completed")}_{scale}00.png", scale);
                if (uncertain)
                {
                    Assert.False(vm.SendPromptCommand.CanExecute(null));
                    Assert.False(vm.ResetSessionCommand.CanExecute(null));
                    // Only fixture cleanup: reconcile the synthetic execution and release its native-free token.
                    Execute(factory, "UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL");
                    var field = typeof(WorkspaceViewModel).GetField("_retainedWriterLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    Pump(((LLMWorkGUI.Application.Concurrency.ICheckoutLockToken)field.GetValue(vm)!).ReleaseAsync("synthetic fixture reconciled"));
                }
                else
                {
                    var journal = provider.GetRequiredService<IOpenCodeExecutionJournal>();
                    Assert.Equal(vm.LocalSessionId, Pump(journal.ConfirmSessionAsync("project-1", root, vm.NativeSessionId, route.Stored)));
                    Click(host, "Сбросить"); WaitIdle(vm, window);
                    Assert.Equal("2", Scalar(factory, "SELECT COUNT(*) FROM Sessions"));
                    Assert.Equal(vm.LocalSessionId, Scalar(factory, "SELECT Id FROM Sessions WHERE NativeSessionId='native-2'"));
                    Assert.Contains("native-1", vm.AncestryDisplay);
                }
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
            finally
            {
                window?.Close(); Pump(provider.DisposeAsync().AsTask());
                using (var connection = factory.CreateConnection()) SqliteConnection.ClearPool(connection);
                directory.Delete(true);
            }
        });
    }
    private sealed class SyntheticLifecycle : IOpenCodeSessionLifecycleService
    {
        private int _sessions;
        private string? DirectoryPath { get; set; }
        public int Dispatches { get; private set; }
        public Func<string, OpenCodePromptRequest, TurnResult>? OnExecute { get; set; }
        public Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            DirectoryPath = request.Directory;
            return Task.FromResult(new OpenCodeSessionResponse { Id = "native-" + ++_sessions });
        }
        public Task<OpenCodeSessionResponse> ContinueSessionAsync(string sessionId, SessionBinding binding, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenCodeSessionResponse { Id = sessionId });
        public async Task<TurnResult> ExecuteTurnAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(request.DispatchAuthorization);
            var root = DirectoryPath ?? throw new InvalidOperationException("Synthetic native directory was not confirmed.");
            var uri = new Uri("http://127.0.0.1:54321/session/" + Uri.EscapeDataString(sessionId)
                + "/prompt_async?directory=" + Uri.EscapeDataString(root));
            Assert.True(await request.DispatchAuthorization(sessionId, request, uri, cancellationToken));
            Dispatches++; return OnExecute!(sessionId, request);
        }
        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<OpenCodeSessionResponse> ResetSessionAsync(string oldSessionId, OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) =>
            CreateAndConfirmSessionAsync(request, cancellationToken);
        public IReadOnlyList<string> GetAncestry(string sessionId) => new[] { "native-1" };
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
        if (!task.IsCompleted)
        { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static void Capture(FrameworkElement root, string filename, int scale)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth * scale, (int)root.ActualHeight * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, filename));
    }
}
