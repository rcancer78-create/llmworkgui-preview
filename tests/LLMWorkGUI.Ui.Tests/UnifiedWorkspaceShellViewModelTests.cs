using System;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class UnifiedWorkspaceShellViewModelTests
{
    [Fact]
    public void ToggleLeftPaneCommand_CollapsesAndRestoresTheNavigationPane()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        Assert.True(shell.IsLeftPaneVisible);
        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, shell.LeftPaneGridLength.Value);

        shell.ToggleLeftPaneCommand.Execute(null);

        Assert.False(shell.IsLeftPaneVisible);
        Assert.Equal(0, shell.LeftPaneGridLength.Value);
        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, shell.LeftPaneWidth);

        shell.ToggleLeftPaneCommand.Execute(null);

        Assert.True(shell.IsLeftPaneVisible);
        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, shell.LeftPaneGridLength.Value);
    }

    [Fact]
    public void ToggleRightPaneCommand_CollapsesAndRestoresTheInspectorPane()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        Assert.True(shell.IsRightPaneVisible);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, shell.RightPaneGridLength.Value);

        shell.ToggleRightPaneCommand.Execute(null);

        Assert.False(shell.IsRightPaneVisible);
        Assert.Equal(0, shell.RightPaneGridLength.Value);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, shell.RightPaneWidth);

        shell.ToggleRightPaneCommand.Execute(null);

        Assert.True(shell.IsRightPaneVisible);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, shell.RightPaneGridLength.Value);
    }

    [Fact]
    public void CollapsingBothPanes_EntersFocusModeWithoutDroppingTheCenterScreen()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var shell = new UnifiedWorkspaceShellViewModel(main);

        shell.ToggleLeftPaneCommand.Execute(null);
        shell.ToggleRightPaneCommand.Execute(null);

        Assert.True(shell.IsFocusMode);
        Assert.Equal(0, shell.LeftPaneGridLength.Value);
        Assert.Equal(0, shell.RightPaneGridLength.Value);
        Assert.NotNull(shell.ActiveScreen);
        Assert.Same(main.CurrentScreen, shell.ActiveScreen);
        Assert.Equal(10, shell.Screens.Count);
    }

    [Fact]
    public void CommandPalette_ContainsTheDefaultSetAndNavigatesTheShell()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        // 18 Phase 11A/11B commands plus the Phase 11C onboarding and workflow-console entries.
        Assert.Equal(20, shell.CommandPalette.Items.Count);
        Assert.Contains(shell.CommandPalette.Items, item => item.Id == "help.onboarding");
        Assert.Contains(shell.CommandPalette.Items, item => item.Id == "workflow.console");
        Assert.Contains(shell.CommandPalette.Items, item => item.Id == "workflow.console" && item.Title == "Открыть консоль сценариев");
        Assert.Contains(shell.CommandPalette.Items, item => item.Id == "workflow.import" && item.Title == "Импорт пакета сценария");
        Assert.Contains(shell.CommandPalette.Items, item => item.Id == "workflow.export" && item.Title == "Экспорт пакета сценария");
        Assert.False(shell.CommandPalette.IsOpen);

        shell.OpenCommandPaletteCommand.Execute(null);

        Assert.True(shell.CommandPalette.IsOpen);

        shell.CommandPalette.SearchQuery = "здоровья";
        shell.CommandPalette.ExecuteSelectedCommand.Execute(null);

        Assert.Equal(ScreenId.HealthCenter, shell.ActiveScreenId);
        Assert.Contains("Центр здоровья", shell.ShellNotice, StringComparison.Ordinal);
        Assert.False(shell.CommandPalette.IsOpen);
    }

    [Fact]
    public void ApplyPersistedLayout_RestoresGeometryActiveScreenAndTheme()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var persistence = new StubLayoutPersistenceService
        {
            State = new ShellLayoutState
            {
                LeftPaneWidth = 264,
                RightPaneWidth = 344,
                IsLeftPaneVisible = false,
                IsRightPaneVisible = true,
                ActiveScreen = nameof(ScreenId.Sessions),
                Theme = nameof(AppTheme.Dark)
            }
        };

        var shell = new UnifiedWorkspaceShellViewModel(main, persistence);

        shell.ApplyPersistedLayout();

        Assert.False(shell.IsLeftPaneVisible);
        Assert.True(shell.IsRightPaneVisible);
        Assert.Equal(264, shell.LeftPaneWidth);
        Assert.Equal(344, shell.RightPaneWidth);
        Assert.Equal(ScreenId.Sessions, main.CurrentScreenId);
        Assert.Equal(ScreenId.Sessions, shell.ActiveScreenId);
        Assert.True(main.ThemeSelector.IsDarkSelected);
    }

    [Fact]
    public void ApplyPersistedLayout_ClampsUnreasonableWidthsToTheDefaults()
    {
        using var provider = UiTestHost.CreateProvider();
        var persistence = new StubLayoutPersistenceService
        {
            State = new ShellLayoutState
            {
                LeftPaneWidth = -5,
                RightPaneWidth = double.NaN
            }
        };
        var shell = new UnifiedWorkspaceShellViewModel(
            provider.GetRequiredService<MainWindowViewModel>(),
            persistence);

        shell.ApplyPersistedLayout();

        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, shell.LeftPaneWidth);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, shell.RightPaneWidth);
    }

    [Fact]
    public void ApplyPersistedLayout_WithoutPersistenceService_KeepsTheDefaults()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = new UnifiedWorkspaceShellViewModel(provider.GetRequiredService<MainWindowViewModel>());

        shell.ApplyPersistedLayout();

        Assert.True(shell.IsLeftPaneVisible);
        Assert.True(shell.IsRightPaneVisible);
        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, shell.LeftPaneWidth);
        Assert.Equal(ScreenId.Workspace, shell.ActiveScreenId);
    }

    [Fact]
    public void PersistLayout_StoresCollapseStateWidthsActiveScreenAndTheme()
    {
        using var provider = UiTestHost.CreateProvider();
        var persistence = new StubLayoutPersistenceService();
        var shell = new UnifiedWorkspaceShellViewModel(
            provider.GetRequiredService<MainWindowViewModel>(),
            persistence);

        shell.ToggleLeftPaneCommand.Execute(null);
        shell.SetPaneWidths(252, 336);
        shell.NavigateTo(ScreenId.HealthCenter);
        Assert.Equal("Открыт экран: Центр здоровья.", shell.ShellNotice);
        shell.SetTheme(AppTheme.Dark);

        Assert.True(persistence.SaveCount > 0);
        Assert.False(persistence.State.IsLeftPaneVisible);
        Assert.True(persistence.State.IsRightPaneVisible);
        Assert.Equal(252, persistence.State.LeftPaneWidth);
        Assert.Equal(336, persistence.State.RightPaneWidth);
        Assert.Equal(nameof(ScreenId.HealthCenter), persistence.State.ActiveScreen);
        Assert.Equal(nameof(AppTheme.Dark), persistence.State.Theme);
    }

    [Fact]
    public void SetPaneWidths_ClampsExtremesToTheSupportedRange()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        shell.SetPaneWidths(10, 5000);

        Assert.Equal(UnifiedWorkspaceShellViewModel.MinLeftPaneWidth, shell.LeftPaneWidth);
        Assert.Equal(UnifiedWorkspaceShellViewModel.MaxRightPaneWidth, shell.RightPaneWidth);
    }

    [Fact]
    public void CompactWidgets_ProjectQuotaBreakerAndCliState()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var shell = new UnifiedWorkspaceShellViewModel(main);

        Assert.Equal(main.StatusBar.QuotaSummary, shell.QuotaSummary);
        Assert.Equal(main.StatusBar.QuotaFreshness, shell.QuotaFreshness);
        Assert.Equal(main.StatusBar.HealthIndicator, shell.BreakerHealthIndicator);
        Assert.Equal(main.StatusBar.HealthDetail, shell.BreakerHealthDetail);
        Assert.Equal(main.CliStatus.DetectionSummary, shell.CliDetectionIndicator);
    }

    [Fact]
    public async Task CliDetectionTestPending_RechecksThroughTheShellShortcut()
    {
        var detection = FakeCliDetectionService.Degraded();
        using var provider = UiTestHost.CreateProvider(detection);
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var shell = new UnifiedWorkspaceShellViewModel(main);

        await main.InitializeAsync();

        Assert.Equal(1, detection.DetectCallCount);
        Assert.Contains("Обнаружено CLI", shell.CliDetectionIndicator, StringComparison.Ordinal);
        Assert.Equal(main.CliStatus.DetectionSummary, shell.CliDetectionIndicator);

        shell.RefreshCli();

        Assert.Contains("обновлены", shell.ShellNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToggleTheme_SwitchesBetweenDarkAndLight()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var persistence = new StubLayoutPersistenceService();
        var shell = new UnifiedWorkspaceShellViewModel(main, persistence);

        shell.SetTheme(AppTheme.Dark);
        Assert.True(main.ThemeSelector.IsDarkSelected);

        shell.ToggleThemeCommand.Execute(null);

        Assert.True(main.ThemeSelector.IsLightSelected);
        Assert.True(persistence.SaveCount >= 1);
        Assert.Equal(nameof(AppTheme.Light), persistence.State.Theme);
    }

    [Fact]
    public void AddUnifiedWorkspaceShell_RegistersTheShellAndItsPersistence()
    {
        var services = new ServiceCollection();

        services.AddUnifiedWorkspaceShell();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILayoutPersistenceService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(UnifiedWorkspaceShellViewModel));
    }

    private static UnifiedWorkspaceShellViewModel CreateShell(ServiceProvider provider) =>
        new(provider.GetRequiredService<MainWindowViewModel>(), new StubLayoutPersistenceService());

    private sealed class StubLayoutPersistenceService : ILayoutPersistenceService
    {
        public ShellLayoutState State { get; set; } = new();

        public int SaveCount { get; private set; }

        public ShellLayoutState Load() => State;

        public void Save(ShellLayoutState state)
        {
            State = state;
            SaveCount++;
        }
    }
}
