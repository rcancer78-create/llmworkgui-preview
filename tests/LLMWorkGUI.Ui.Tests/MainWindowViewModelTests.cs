using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly (ScreenId Id, string Title, string Shortcut)[] ExpectedScreens =
    {
        (ScreenId.Workspace, "Рабочая область", "Ctrl+1"),
        (ScreenId.Projects, "Проекты", "Ctrl+2"),
        (ScreenId.ProvidersAccounts, "Провайдеры и аккаунты", "Ctrl+3"),
        (ScreenId.Models, "Модели", "Ctrl+4"),
        (ScreenId.Quotas, "Квоты", "Ctrl+5"),
        (ScreenId.Sessions, "Сессии", "Ctrl+6"),
        (ScreenId.Runs, "Запуски", "Ctrl+7"),
        (ScreenId.Workflows, "Процессы", "Ctrl+8"),
        (ScreenId.HealthCenter, "Центр здоровья", "Ctrl+9"),
        (ScreenId.SettingsDiagnostics, "Настройки / Диагностика", "Ctrl+0")
    };

    [Fact]
    public void Constructor_ExposesTenNormativeScreensInOrder()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Equal(ExpectedScreens.Length, viewModel.Screens.Count);
        Assert.Equal(ExpectedScreens.Length, viewModel.NavigationItems.Count);

        for (var index = 0; index < ExpectedScreens.Length; index++)
        {
            var expected = ExpectedScreens[index];
            var actual = viewModel.Screens[index];

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Shortcut, actual.Shortcut);
        }
    }

    [Fact]
    public void Constructor_StartsWithWorkspaceSelected()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Equal(ScreenId.Workspace, viewModel.CurrentScreenId);
        Assert.Same(viewModel.Screens[0], viewModel.CurrentScreen);
        Assert.Same(viewModel.NavigationItems[0], viewModel.SelectedNavigationItem);
        Assert.True(viewModel.NavigationItems[0].IsSelected);
        Assert.False(viewModel.NavigationItems[1].IsSelected);
    }

    [Fact]
    public void NavigateTo_ChangesCurrentScreenAndSelectionFlags()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        viewModel.NavigateTo(ScreenId.HealthCenter);

        Assert.Equal(ScreenId.HealthCenter, viewModel.CurrentScreenId);
        Assert.True(viewModel.NavigationItems.Single(item => item.Id == ScreenId.HealthCenter).IsSelected);
        Assert.All(
            viewModel.NavigationItems.Where(item => item.Id != ScreenId.HealthCenter),
            item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void SelectedNavigationItem_UpdatesCurrentScreen()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        var sessionsItem = viewModel.NavigationItems.Single(item => item.Id == ScreenId.Sessions);
        viewModel.SelectedNavigationItem = sessionsItem;

        Assert.Equal(ScreenId.Sessions, viewModel.CurrentScreenId);
        Assert.Same(sessionsItem.Screen, viewModel.CurrentScreen);
    }

    [Fact]
    public void NavigateCommand_AcceptsScreenIdNameAndNavigationItem()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        viewModel.NavigateCommand.Execute(ScreenId.Runs);
        Assert.Equal(ScreenId.Runs, viewModel.CurrentScreenId);

        viewModel.NavigateCommand.Execute("Workflows");
        Assert.Equal(ScreenId.Workflows, viewModel.CurrentScreenId);

        viewModel.NavigateCommand.Execute(viewModel.NavigationItems[0]);
        Assert.Equal(ScreenId.Workspace, viewModel.CurrentScreenId);
    }

    [Fact]
    public void NavigateCommand_WithUnsupportedParameter_Throws()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Throws<ArgumentException>(() => viewModel.NavigateCommand.Execute(42));
    }

    [Fact]
    public void SelectedNavigationItem_WithForeignItem_IsIgnored()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();
        var foreignItem = new NavigationItemViewModel(viewModel.Screens[3]);

        viewModel.SelectedNavigationItem = foreignItem;

        Assert.Equal(ScreenId.Workspace, viewModel.CurrentScreenId);
    }

    [Fact]
    public async Task InitializeAsync_RefreshesCliDetectionAndDegradedStatus()
    {
        var detectionService = FakeCliDetectionService.Degraded();
        using var provider = UiTestHost.CreateProvider(detectionService);
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        await viewModel.InitializeAsync();

        Assert.Equal(1, detectionService.DetectCallCount);
        Assert.True(viewModel.CliStatus.IsChecked);
        Assert.True(viewModel.CliStatus.IsDegraded);
        Assert.True(viewModel.StatusBar.IsDegraded);
        Assert.Equal(2, viewModel.CliStatus.Tools.Count);
    }

    [Fact]
    public void StatusBar_ExposesNotReportedPlaceholdersAndUnknownQuota()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();
        var statusBar = viewModel.StatusBar;

        Assert.Equal("Not reported", statusBar.Provider);
        Assert.Equal("Not reported", statusBar.Account);
        Assert.Equal("Not reported", statusBar.Model);
        Assert.Equal("Not reported", statusBar.Reasoning);
        Assert.Equal("Not reported", statusBar.Speed);
        Assert.Equal("Not reported", statusBar.LocalSessionId);
        Assert.Equal("Not reported", statusBar.NativeSessionId);
        Assert.Equal("Not reported", statusBar.SessionConfirmation);
        Assert.Equal("Not reported", statusBar.ProjectWorkspace);
        Assert.Equal("Not reported", statusBar.WorkflowRoleStage);
        Assert.Equal("Not reported", statusBar.RoutingDecisionReason);
        Assert.Equal("Ожидание", statusBar.ExecutionState);
        Assert.Equal("Неизвестно", statusBar.QuotaSummary);
        Assert.Equal("Никогда", statusBar.QuotaFreshness);
        Assert.Equal("Норма", statusBar.HealthIndicator);
    }

    [Fact]
    public void ThemeSelector_IsRegisteredAndTracksThemeService()
    {
        using var provider = UiTestHost.CreateProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.True(viewModel.ThemeSelector.IsSystemSelected);
        Assert.False(viewModel.ThemeSelector.IsDarkSelected);
        Assert.False(viewModel.ThemeSelector.IsLightSelected);
    }
}
