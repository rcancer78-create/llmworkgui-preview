using System;
using System.Collections.Generic;
using System.Linq;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CommandPaletteViewModelTests
{
    [Fact]
    public void Open_ClearsPreviousQueryAndSelectsFirstCommand()
    {
        var first = CreateItem("first", "First command");
        var second = CreateItem("second", "Second command");
        var viewModel = new CommandPaletteViewModel(new[] { first, second });

        viewModel.Open();
        viewModel.SearchQuery = "second";
        viewModel.Close();

        viewModel.Open();

        Assert.True(viewModel.IsOpen);
        Assert.Equal(string.Empty, viewModel.SearchQuery);
        Assert.Equal(2, viewModel.FilteredItems.Count);
        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.Same(first, viewModel.SelectedItem);
    }

    [Fact]
    public void SearchQuery_FiltersCaseInsensitivelyAcrossTitleDescriptionAndCategory()
    {
        var health = CreateItem(
            "health",
            "Open Health Center",
            "Inspect circuit breakers and recovery probes.",
            CommandPaletteCategories.Actions);
        var dark = CreateItem(
            "dark",
            "Switch to dark theme",
            "Apply the dark palette.",
            CommandPaletteCategories.Theme);
        var workspace = CreateItem("workspace", "Workspace", "Current work.", CommandPaletteCategories.Navigation);
        var viewModel = new CommandPaletteViewModel(new[] { health, dark, workspace });

        viewModel.Open();

        viewModel.SearchQuery = "HEALTH";
        Assert.Equal(new[] { "health" }, viewModel.FilteredItems.Select(item => item.Id));

        viewModel.SearchQuery = "recovery";
        Assert.Equal(new[] { "health" }, viewModel.FilteredItems.Select(item => item.Id));

        viewModel.SearchQuery = "theme";
        Assert.Equal(new[] { "dark" }, viewModel.FilteredItems.Select(item => item.Id));

        viewModel.SearchQuery = "work";
        Assert.Equal(new[] { "workspace" }, viewModel.FilteredItems.Select(item => item.Id));
    }

    [Fact]
    public void SearchQuery_WithoutMatches_ClearsSelectionAndReportsEmptyState()
    {
        var viewModel = new CommandPaletteViewModel(new[] { CreateItem("only", "Only command") });

        viewModel.Open();
        viewModel.SearchQuery = "definitely-not-a-command";

        Assert.Empty(viewModel.FilteredItems);
        Assert.False(viewModel.HasResults);
        Assert.Equal(-1, viewModel.SelectedIndex);
        Assert.Null(viewModel.SelectedItem);
        Assert.Equal("Команды по запросу не найдены.", CommandPaletteViewModel.NoResultsMessage);
        Assert.Equal(CommandPaletteViewModel.NoResultsMessage, viewModel.ResultSummary);
    }

    [Fact]
    public void ResultSummary_WithMatches_ReturnsRussianCount()
    {
        var viewModel = new CommandPaletteViewModel(new[]
        {
            CreateItem("a", "Alpha"),
            CreateItem("b", "Bravo")
        });

        viewModel.Open();
        Assert.True(viewModel.HasResults);
        Assert.Equal("Команд: 2", viewModel.ResultSummary);
    }

    [Fact]
    public void SelectNextAndPrevious_WrapCyclically()
    {
        var viewModel = new CommandPaletteViewModel(new[]
        {
            CreateItem("a", "Alpha"),
            CreateItem("b", "Bravo"),
            CreateItem("c", "Charlie")
        });

        viewModel.Open();

        Assert.Equal(0, viewModel.SelectedIndex);

        viewModel.SelectNextCommand.Execute(null);
        viewModel.SelectNextCommand.Execute(null);
        Assert.Equal(2, viewModel.SelectedIndex);

        viewModel.SelectNextCommand.Execute(null);
        Assert.Equal(0, viewModel.SelectedIndex);

        viewModel.SelectPreviousCommand.Execute(null);
        Assert.Equal(2, viewModel.SelectedIndex);

        viewModel.SelectPreviousCommand.Execute(null);
        Assert.Equal(1, viewModel.SelectedIndex);
    }

    [Fact]
    public void ExecuteSelected_RunsTheSelectedCommandAndClosesThePalette()
    {
        var executions = 0;
        var runnable = CreateItem("run", "Runnable command", execute: () => executions++);
        var viewModel = new CommandPaletteViewModel(new[] { runnable, CreateItem("other", "Other command") });

        viewModel.Open();
        viewModel.SearchQuery = "runnable";
        viewModel.ExecuteSelectedCommand.Execute(null);

        Assert.Equal(1, executions);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void ExecuteSelected_WithoutMatches_DoesNothing()
    {
        var executions = 0;
        var viewModel = new CommandPaletteViewModel(new[]
        {
            CreateItem("run", "Runnable command", execute: () => executions++)
        });

        viewModel.Open();
        viewModel.SearchQuery = "missing";
        viewModel.ExecuteSelectedCommand.Execute(null);

        Assert.Equal(0, executions);
        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void CreateDefault_RegistersNavigationThemesCliHealthAndWorkflowCommands()
    {
        var host = new StubCommandHost();

        var viewModel = CommandPaletteViewModel.CreateDefault(host);

        var navigationItems = viewModel.Items
            .Where(item => item.Category == CommandPaletteCategories.Navigation)
            .ToArray();
        var themeItems = viewModel.Items
            .Where(item => item.Category == CommandPaletteCategories.Theme)
            .ToArray();
        var actionItems = viewModel.Items
            .Where(item => item.Category == CommandPaletteCategories.Actions)
            .ToArray();
        var workflowItems = viewModel.Items
            .Where(item => item.Category == CommandPaletteCategories.Workflow)
            .ToArray();

        Assert.Equal(10, navigationItems.Length);
        Assert.Equal(Enum.GetValues<ScreenId>().Length, navigationItems.Length);
        Assert.All(navigationItems, item => Assert.StartsWith("Ctrl+", item.ShortcutDisplay, StringComparison.Ordinal));

        Assert.Equal(2, themeItems.Length);
        Assert.Contains(themeItems, item => item.Id == "theme.dark");
        Assert.Contains(themeItems, item => item.Id == "theme.light");

        Assert.Contains(actionItems, item => item.Id == "actions.refresh-cli" && item.ShortcutDisplay == "F5");
        Assert.Contains(actionItems, item =>
            item.Id == "actions.health-center" &&
            item.Title == "Открыть Центр здоровья" &&
            item.Description == "Проверить области предохранителей, зонды восстановления и затронутые сессии.");

        Assert.Contains(workflowItems, item => item.Id == "workflow.studio");
        Assert.Contains(workflowItems, item => item.Id == "workflow.activity-monitor");
        Assert.Contains(workflowItems, item => item.Id == "workflow.import");
        Assert.Contains(workflowItems, item => item.Id == "workflow.export");
    }

    [Fact]
    public void CreateDefault_CommandsReachTheHostWithoutAMouse()
    {
        var host = new StubCommandHost();
        var viewModel = CommandPaletteViewModel.CreateDefault(host);

        viewModel.Open();
        viewModel.SearchQuery = "Sessions";
        viewModel.ExecuteSelectedCommand.Execute(null);

        viewModel.SearchQuery = "тёмную тему";
        viewModel.ExecuteSelectedCommand.Execute(null);

        viewModel.SearchQuery = "статус CLI";
        viewModel.ExecuteSelectedCommand.Execute(null);

        viewModel.SearchQuery = "Workflow Studio";
        viewModel.ExecuteSelectedCommand.Execute(null);

        Assert.Contains(ScreenId.Sessions, host.Navigated);
        Assert.Contains(AppTheme.Dark, host.Themes);
        Assert.Equal(1, host.RefreshCliCount);
        Assert.Equal(1, host.WorkflowStudioCount);
    }

    private static CommandPaletteItemViewModel CreateItem(
        string id,
        string title,
        string? description = null,
        string? category = null,
        string shortcut = "",
        Action? execute = null) =>
        new(
            id,
            title,
            description ?? $"{title} description",
            category ?? CommandPaletteCategories.Actions,
            shortcut,
            new RelayCommand(() => execute?.Invoke()));

    private sealed class StubCommandHost : ICommandPaletteCommandHost
    {
        public List<ScreenId> Navigated { get; } = new();

        public List<AppTheme> Themes { get; } = new();

        public int RefreshCliCount { get; private set; }

        public int WorkflowStudioCount { get; private set; }

        public int WorkflowActivityMonitorCount { get; private set; }

        public int ImportWorkflowCount { get; private set; }

        public int ExportWorkflowCount { get; private set; }

        public IReadOnlyList<ScreenViewModel> Screens { get; } = CreateScreens();

        public void NavigateTo(ScreenId screenId) => Navigated.Add(screenId);

        public void SetTheme(AppTheme theme) => Themes.Add(theme);

        public void RefreshCli() => RefreshCliCount++;

        public void OpenWorkflowStudio() => WorkflowStudioCount++;

        public void OpenWorkflowActivityMonitor() => WorkflowActivityMonitorCount++;

        public void ImportWorkflow() => ImportWorkflowCount++;

        public void ExportWorkflow() => ExportWorkflowCount++;

        private static IReadOnlyList<ScreenViewModel> CreateScreens() =>
            Enum.GetValues<ScreenId>()
                .Select(id => (ScreenViewModel)new CatalogScreenViewModel(
                    id,
                    id.ToString(),
                    "Ctrl+1",
                    $"{id} description",
                    $"No {id} yet.",
                    $"{id} arrives later."))
                .ToArray();
    }
}
