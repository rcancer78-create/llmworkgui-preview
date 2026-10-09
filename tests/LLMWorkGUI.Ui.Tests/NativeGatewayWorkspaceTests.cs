using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Account configuration UI isolation")]
public sealed partial class NativeGatewayWorkspaceTests
{
    private static readonly NativeGatewayRouteOption Route = new("route", new("provider", "account", "model", "work", "native-model"), "Codex", "Рабочий аккаунт", "Модель");
    private static Project Project(string id = "project") => new(id, "Тестовый проект", "D:\\workspace", null, false, false, null, null, DataClassification.PublicSource);
    private static NativeGatewayTurnResult Success() => new("local-session", "execution", ExecutionState.Succeeded, ExecutionFailureReason.None, "Ответ модели", false);

    [Fact]
    public void SendPinsSelectedBindingAndRunsOffDispatcherWithoutAutomaticSelection()
    {
        StaTestRunner.Run(() =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var fixture = Ready();
            Assert.Null(fixture.Vm.SelectedRoute); Assert.False(fixture.Vm.CanSend);
            fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            Pump(fixture.Vm.SendAsync());
            Assert.NotEqual(uiThread, fixture.Turns.ThreadId);
            Assert.Equal(Route.Binding, fixture.Turns.Request!.ExpectedBinding);
            Assert.Equal("project", fixture.Turns.Request.ProjectId);
            Assert.Equal("D:\\workspace", fixture.Turns.Request.RootPath);
            Assert.Equal("Запрос", fixture.Turns.Request.Prompt);
            Assert.Equal("Ответ модели", fixture.Vm.Response);
            Assert.Contains("Нативная идентичность ответа не подтверждена", fixture.Vm.Outcome);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Outcome);
            Assert.Contains("Ответ получен", fixture.Vm.Outcome);
            Assert.DoesNotContain("Доставка неизвестна", fixture.Vm.Outcome);
            Assert.DoesNotContain("Повтор", fixture.Vm.Outcome);
            Assert.DoesNotContain("local-session", fixture.Vm.Outcome);
            Assert.DoesNotContain("ObservedRoute", fixture.Vm.Outcome);
            Assert.DoesNotContain("обновите", fixture.Vm.Outcome);
            Assert.Contains("local-session", fixture.Vm.ExecutionDisplay);
            Assert.Equal(1, fixture.Turns.Calls);
        });
    }

    [Fact]
    public void CancellationKeepsPanelBusyUntilServiceReturnsAndDoesNotRepeatRequest()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource<NativeGatewayTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken received = default;
            fixture.Turns.Action = (_, token) => { received = token; entered.SetResult(); return complete.Task; };
            var sending = fixture.Vm.SendAsync(); Pump(entered.Task);
            try
            {
                Assert.True(fixture.Vm.IsBusy); Assert.False(fixture.Vm.RefreshCommand.CanExecute(null));
                fixture.Vm.SelectedRoute = null; fixture.Vm.PromptInput = "Другой запрос";
                Assert.Equal(Route, fixture.Vm.SelectedRoute); Assert.Equal("Запрос", fixture.Vm.PromptInput);
                Pump(fixture.Vm.SendAsync()); Assert.Equal(1, fixture.Turns.Calls);
                Assert.Contains("Выполняется запрос LLMGateway", fixture.Vm.Message);
                Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
                Assert.DoesNotContain("доставлен", fixture.Vm.Message);
                Assert.DoesNotContain("отказом", fixture.Vm.Message);
                Assert.DoesNotContain("local-session", fixture.Vm.Message);
                Assert.DoesNotContain("идентичность", fixture.Vm.Message);
                fixture.Vm.CancelCommand.Execute(null);
                Assert.True(received.IsCancellationRequested); Assert.True(fixture.Vm.IsBusy);
                Assert.False(fixture.Vm.CanCancel); Assert.False(fixture.Vm.CanSend);
                Assert.Contains("ещё не подтверждена", fixture.Vm.Message);
                Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
                Assert.Contains("Запрос мог быть отправлен", fixture.Vm.Message);
                Assert.Contains("Доставка неизвестна", fixture.Vm.Message);
                Assert.Contains("Повтор небезопасен", fixture.Vm.Message);
                Assert.DoesNotContain("не отправлялся", fixture.Vm.Message);
                Assert.DoesNotContain("не доставлялся", fixture.Vm.Message);
                Assert.DoesNotContain("обновите маршруты", fixture.Vm.Message);
                Assert.DoesNotContain("local-session", fixture.Vm.Message);
                Assert.DoesNotContain("Состояние здоровья не изменялось", fixture.Vm.Message);
            }
            finally
            {
                complete.TrySetResult(Success() with { State = ExecutionState.Ambiguous, FailureReason = ExecutionFailureReason.UserCancelled, RequiresReconciliation = true });
                Pump(sending);
            }
            Assert.False(fixture.Vm.IsBusy); Assert.False(fixture.Vm.CanSend);
            Assert.Empty(fixture.Vm.Response); Assert.Contains("Блокировка проекта сохранена", fixture.Vm.Outcome);
            Assert.Equal(1, fixture.Turns.Calls);
        });
    }

    [Fact]
    public void ProjectSwitchAfterRefreshRefusesDispatch()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Projects.Current = Project("another");
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(0, fixture.Turns.Calls); Assert.False(fixture.Vm.CanSend);
            Assert.Contains("проект изменился", fixture.Vm.Message);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
            Assert.Contains("Запрос не доставлялся", fixture.Vm.Message);
            Assert.Contains("прочитать заново", fixture.Vm.Message);
            Assert.DoesNotContain("перед отправкой", fixture.Vm.Message);
            Assert.DoesNotContain("Повтор небезопасен", fixture.Vm.Message);
            Assert.DoesNotContain("мог быть доставлен", fixture.Vm.Message);
            Assert.DoesNotContain("могла получить", fixture.Vm.Message);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.ExecutionDisplay);
        });
    }

    [Fact]
    public void RefreshFailureClearsStaleChoicesAndHidesExceptionDetails()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Catalog.Fail = true; Pump(fixture.Vm.RefreshAsync());
            Assert.Empty(fixture.Vm.Routes); Assert.Null(fixture.Vm.SelectedRoute); Assert.False(fixture.Vm.CanSend);
            Assert.DoesNotContain("private-fixture", fixture.Vm.Message);
            Pump(fixture.Vm.SendAsync()); Assert.Equal(0, fixture.Turns.Calls);
        });
    }

    [Fact]
    public void ViewOnlyOrMissingGuardCannotSendEvenThroughDirectMethod()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Guard.ViewOnly = true; Assert.False(fixture.Vm.CanSend);
            Pump(fixture.Vm.SendAsync()); Assert.Equal(0, fixture.Turns.Calls);
            var missing = new NativeGatewayWorkspaceViewModel(fixture.Turns, fixture.Catalog, new(fixture.Projects), null);
            Pump(missing.RefreshAsync()); missing.SelectedRoute = Route; missing.PromptInput = "Запрос";
            Assert.False(missing.CanSend); Pump(missing.SendAsync()); Assert.Equal(0, fixture.Turns.Calls);
        });
    }

    [Fact]
    public void ServiceExceptionRequiresRefreshWithoutLeakingNativeDetails()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => throw new IOException("private-fixture");
            Pump(fixture.Vm.SendAsync());
            Assert.DoesNotContain("private-fixture", fixture.Vm.Message);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
            Assert.Contains("Запрос мог быть доставлен", fixture.Vm.Message);
            Assert.Contains("Повтор небезопасен", fixture.Vm.Message);
            Assert.Contains("Состояние здоровья не изменялось", fixture.Vm.Message);
            Assert.DoesNotContain("обновите маршруты", fixture.Vm.Message);
            Assert.DoesNotContain("Проверьте журнал", fixture.Vm.Message);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.DoesNotContain(nameof(IOException), fixture.Vm.Message);
            Assert.False(fixture.Vm.CanSend); Assert.Empty(fixture.Vm.Response);
            Assert.Empty(fixture.Vm.ExecutionDisplay);
            Pump(fixture.Vm.SendAsync()); Assert.Equal(1, fixture.Turns.Calls);
        });
    }

    [Fact]
    public void SendThrownBeforeCall_NamesRouteAndSaysNotDelivered()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Projects.ListFailure = new InvalidOperationException("api_key=synthetic-gateway-secret");
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(0, fixture.Turns.Calls);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
            Assert.Contains("Запрос не доставлялся", fixture.Vm.Message);
            Assert.DoesNotContain("Повтор небезопасен", fixture.Vm.Message);
            Assert.DoesNotContain("мог быть доставлен", fixture.Vm.Message);
            Assert.DoesNotContain("могла получить", fixture.Vm.Message);
            Assert.DoesNotContain("обновите маршруты", fixture.Vm.Message);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.DoesNotContain("synthetic-gateway-secret", fixture.Vm.Message);
            Assert.DoesNotContain(nameof(InvalidOperationException), fixture.Vm.Message);
            Assert.Empty(fixture.Vm.Response);
            Assert.Empty(fixture.Vm.ExecutionDisplay);
        });
    }

    [Fact]
    public void ReturnedFailure_NamesRouteAndStatesDeliveryFromTheResult()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => Task.FromResult(Success() with
            {
                State = ExecutionState.Failed,
                FailureReason = ExecutionFailureReason.InternalError,
                Content = null,
                RequiresReconciliation = false
            });
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(1, fixture.Turns.Calls);
            var notice = fixture.Vm.Outcome + "\n" + fixture.Vm.Message;
            Assert.Contains("Маршрут route (LLMGateway, native-model)", notice);
            Assert.Contains("Доставка неизвестна", notice);
            Assert.Contains("Повтор небезопасен", notice);
            Assert.DoesNotContain("обновите маршруты", notice);
            Assert.DoesNotContain("Проверьте журнал", notice);
            Assert.DoesNotContain("неопредел", notice);
            Assert.DoesNotContain("мог быть доставлен", notice);
            Assert.DoesNotContain("отправлен и", notice);
            Assert.DoesNotContain("local-session", fixture.Vm.Outcome);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.Response);
            Assert.False(fixture.Vm.CanSend);
        });
    }

    [Fact]
    public void ReturnedAmbiguous_NamesRouteAndUnknownTerminalResult()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => Task.FromResult(Success() with
            {
                State = ExecutionState.Ambiguous,
                FailureReason = ExecutionFailureReason.InternalError,
                Content = null,
                RequiresReconciliation = true
            });
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(1, fixture.Turns.Calls);
            var notice = fixture.Vm.Outcome + "\n" + fixture.Vm.Message;
            Assert.Contains("Маршрут route (LLMGateway, native-model)", notice);
            Assert.Contains("Запрос мог быть отправлен", notice);
            Assert.Contains("Итоговый результат неизвестен", notice);
            Assert.Contains("Повтор небезопасен", notice);
            Assert.Contains("Блокировка проекта сохранена", notice);
            Assert.DoesNotContain("обновите маршруты", notice);
            Assert.DoesNotContain("Доставка неизвестна", notice);
            Assert.DoesNotContain("Состояние здоровья не изменялось", notice);
            Assert.DoesNotContain("не доставлялся", notice);
            Assert.DoesNotContain("local-session", fixture.Vm.Outcome);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.Response);
            Assert.False(fixture.Vm.CanSend);
        });
    }

    [Fact]
    public void ReturnedCancelled_NamesRouteAndUnknownDelivery()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => Task.FromResult(Success() with
            {
                State = ExecutionState.Cancelled,
                FailureReason = ExecutionFailureReason.UserCancelled,
                Content = null,
                RequiresReconciliation = false
            });
            Pump(fixture.Vm.SendAsync());
            var notice = fixture.Vm.Outcome + "\n" + fixture.Vm.Message;
            Assert.Contains("Маршрут route (LLMGateway, native-model)", notice);
            Assert.Contains("отменён", notice);
            Assert.Contains("Доставка неизвестна", notice);
            Assert.DoesNotContain("Повтор небезопасен", notice);
            Assert.DoesNotContain("обновите маршруты", notice);
            Assert.DoesNotContain("до отправки", notice);
            Assert.DoesNotContain("мог быть отправлен", notice);
            Assert.DoesNotContain("завершился отказом", notice);
            Assert.DoesNotContain("Состояние здоровья не изменялось", notice);
            Assert.DoesNotContain("Блокировка проекта сохранена", notice);
            Assert.DoesNotContain("local-session", fixture.Vm.Outcome);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.Response);
        });
    }

    [Fact]
    public void ReturnedTimedOut_NamesRouteAndUnknownDelivery()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => Task.FromResult(Success() with
            {
                State = ExecutionState.TimedOut,
                FailureReason = ExecutionFailureReason.NetworkTimeout,
                Content = null,
                RequiresReconciliation = false
            });
            Pump(fixture.Vm.SendAsync());
            var notice = fixture.Vm.Outcome + "\n" + fixture.Vm.Message;
            Assert.Contains("Маршрут route (LLMGateway, native-model)", notice);
            Assert.Contains("Время ожидания", notice);
            Assert.Contains("Доставка неизвестна", notice);
            Assert.DoesNotContain("Повтор небезопасен", notice);
            Assert.DoesNotContain("обновите маршруты", notice);
            Assert.DoesNotContain("до отправки", notice);
            Assert.DoesNotContain("мог быть отправлен", notice);
            Assert.DoesNotContain("отменён", notice);
            Assert.DoesNotContain("Состояние здоровья не изменялось", notice);
            Assert.DoesNotContain("local-session", fixture.Vm.Outcome);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.Response);
        });
    }

    [Fact]
    public void SendCancelledBeforeCall_NamesRouteAndSaysNotDelivered()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Projects.ListFailure = new OperationCanceledException();
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(0, fixture.Turns.Calls);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
            Assert.Contains("Запрос не доставлялся", fixture.Vm.Message);
            Assert.DoesNotContain("Повтор небезопасен", fixture.Vm.Message);
            Assert.DoesNotContain("Доставка неизвестна", fixture.Vm.Message);
            Assert.DoesNotContain("обновите маршруты", fixture.Vm.Message);
            Assert.DoesNotContain("Запрос LLMGateway отменён", fixture.Vm.Message);
            Assert.DoesNotContain("Состояние здоровья не изменялось", fixture.Vm.Message);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.ExecutionDisplay);
        });
    }

    [Fact]
    public void SendCancelledAfterCallStarted_NamesRouteAndUnknownDelivery()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route; fixture.Vm.PromptInput = "Запрос";
            fixture.Turns.Action = (_, _) => throw new OperationCanceledException();
            Pump(fixture.Vm.SendAsync());
            Assert.Equal(1, fixture.Turns.Calls);
            Assert.Contains("Маршрут route (LLMGateway, native-model)", fixture.Vm.Message);
            Assert.Contains("Доставка неизвестна", fixture.Vm.Message);
            Assert.Contains("Повтор небезопасен", fixture.Vm.Message);
            Assert.DoesNotContain("Запрос не доставлялся", fixture.Vm.Message);
            Assert.DoesNotContain("Запрос LLMGateway отменён", fixture.Vm.Message);
            Assert.DoesNotContain("обновите маршруты", fixture.Vm.Message);
            Assert.DoesNotContain("мог быть отправлен", fixture.Vm.Message);
            Assert.DoesNotContain("Состояние здоровья не изменялось", fixture.Vm.Message);
            Assert.DoesNotContain("local-session", fixture.Vm.Message);
            Assert.Empty(fixture.Vm.ExecutionDisplay);
            Assert.Empty(fixture.Vm.Response);
        });
    }

    [Fact]
    public void RefreshDropsSelectionIfBindingChangedAndDoesNotSelectAnotherRoute()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready(); fixture.Vm.SelectedRoute = Route;
            fixture.Catalog.Options = [Route with { Binding = Route.Binding with { NativeAccountId = "other" } }];
            Pump(fixture.Vm.RefreshAsync());
            Assert.Null(fixture.Vm.SelectedRoute); Assert.False(fixture.Vm.CanSend);
            Assert.Equal(0, fixture.Turns.Calls);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 1000, 720)]
    [InlineData(AppTheme.Light, 700, 720)]
    public void ShippedPanelComposesLazilyAndButtonsExecuteSelectedRequest(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("native-gateway-workspace-");
            ServiceProvider? provider = null; ISqliteConnectionFactory? factory = null; Window? window = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                var turns = new FakeTurns();
                services.AddSingleton<INativeGatewayTurnService>(turns);
                services.AddSingleton<INativeGatewayRouteCatalog>(new FakeCatalog());
                services.AddSingleton<ILlmGateway>(_ => throw new InvalidOperationException("Native gateway must stay lazy"));
                provider = services.BuildServiceProvider(validateScopes: true);
                factory = provider.GetRequiredService<ISqliteConnectionFactory>();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                Pump(provider.GetRequiredService<IProjectRepository>().UpsertAsync(Project()));
                var workspace = provider.GetRequiredService<WorkspaceViewModel>();
                var vm = provider.GetRequiredService<NativeGatewayWorkspaceViewModel>();
                Assert.Same(vm, workspace.NativeGateway); Assert.True(workspace.HasNativeGatewayPanel);
                Assert.Empty(vm.Routes); Assert.Equal(0, turns.Calls);
                var host = new ContentControl { Content = vm, Margin = new Thickness(16) };
                window = new Window { Width = width, Height = height, Content = host, Title = "LLMGateway" };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/LLMWorkGUI.App;component/Themes/{theme}.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
                window.Show(); Settle(window);
                var refresh = Descendants<Button>(host).Single(b => AutomationProperties.GetName(b) == "Обновить маршруты LLMGateway");
                Assert.True(refresh.IsEnabled); refresh.Command.Execute(null); PumpBusy(vm, window);
                var selector = Descendants<ComboBox>(host).Single();
                selector.SelectedItem = Assert.Single(vm.Routes);
                var prompt = Descendants<TextBox>(host).Single(b => AutomationProperties.GetName(b) == "Текст запроса LLMGateway");
                prompt.Text = "Проверь структуру тестового проекта."; Settle(window);
                var send = Descendants<Button>(host).Single(b => AutomationProperties.GetName(b) == "Отправить запрос LLMGateway");
                Assert.True(send.IsEnabled); send.Command.Execute(null); PumpBusy(vm, window);
                Assert.Equal(1, turns.Calls); Assert.Equal(Route.Binding, turns.Request!.ExpectedBinding);
                Assert.Equal("Ответ модели", vm.Response);
                var response = Descendants<TextBox>(host).Single(b => AutomationProperties.GetName(b) == "Ответ LLMGateway");
                Assert.True(response.IsReadOnly); Assert.Equal(vm.Response, response.Text);
                Assert.True(response.TranslatePoint(new Point(0, response.ActualHeight), window).Y < window.ActualHeight);
                Capture(window, $"gateway_workspace_{theme.ToString().ToLowerInvariant()}.png");
            }
            finally
            {
                window?.Close();
                if (provider is not null) Pump(provider.DisposeAsync().AsTask());
                using var connection = factory?.CreateConnection();
                if (connection is not null) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                var resolved = Path.GetFullPath(directory.FullName);
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
                Directory.Delete(resolved, true);
            }
        });
    }

    private static (NativeGatewayWorkspaceViewModel Vm, FakeProjects Projects, FakeCatalog Catalog, FakeTurns Turns, Guard Guard) Ready()
    {
        var projects = new FakeProjects(); var catalog = new FakeCatalog(); var turns = new FakeTurns(); var guard = new Guard();
        var vm = new NativeGatewayWorkspaceViewModel(turns, catalog, new(projects), guard);
        Pump(vm.RefreshAsync()); return (vm, projects, catalog, turns, guard);
    }
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted, "UI operation timed out"); task.GetAwaiter().GetResult();
    }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static void PumpBusy(NativeGatewayWorkspaceViewModel vm, Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsBusy && DateTime.UtcNow < deadline) Settle(window);
        Assert.False(vm.IsBusy); Settle(window);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string name)
    {
        var root = ScreenshotFile.OutputDirectory;
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        ScreenshotFile.Save(encoder, Path.Combine(root, name));
    }
    private sealed class FakeCatalog : INativeGatewayRouteCatalog
    {
        public bool Fail; public IReadOnlyList<NativeGatewayRouteOption> Options = [Route];
        public Task<IReadOnlyList<NativeGatewayRouteOption>> ListAsync(string projectId, string root, CancellationToken token = default) =>
            Fail ? throw new IOException("private-fixture") : Task.FromResult(Options);
    }
    private sealed class FakeTurns : INativeGatewayTurnService
    {
        public int Calls, ThreadId; public NativeGatewayTurnRequest? Request;
        public Func<NativeGatewayTurnRequest, CancellationToken, Task<NativeGatewayTurnResult>> Action = (_, _) => Task.FromResult(Success());
        public Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken token = default)
        { Calls++; ThreadId = Environment.CurrentManagedThreadId; Request = request; return Action(request, token); }
    }
    private sealed class Guard : IApplicationInstanceGuard
    {
        public bool ViewOnly;
        public string InstanceId => "fixture";
        public bool IsPrimarySupervisor => !ViewOnly;
        public bool IsViewOnly => ViewOnly;
        public void EnsureSupervisorPermitted() { if (ViewOnly) throw new SecondaryInstanceReadOnlyException("Только просмотр."); }
        public void Dispose() { }
    }
    private sealed class FakeProjects : IProjectRepository
    {
        public Project Current = Project();
        public Exception? ListFailure { get; set; }
        public Task UpsertAsync(Project project, CancellationToken token = default) => throw new NotSupportedException();
        public Task<Project?> GetByIdAsync(string id, CancellationToken token = default) => Task.FromResult<Project?>(Current.Id == id ? Current : null);
        public Task<Project?> GetByRootPathAsync(string root, CancellationToken token = default) => Task.FromResult<Project?>(Current.RootPath == root ? Current : null);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken token = default) =>
            ListFailure is not null ? throw ListFailure : Task.FromResult<IReadOnlyList<Project>>([Current]);
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
    }
}
