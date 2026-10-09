using System.Windows.Input;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Command palette state machine (ROADMAP Phase 11). Filtering is case-insensitive across the item's
/// title, description and category, arrow navigation wraps cyclically, and Enter executes the selected
/// command and closes the palette. The default set covers all ten screens plus theme, CLI and workflow
/// actions (ТЗ §7.2).
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    public const string NoResultsMessage = "Команды по запросу не найдены.";
    public const string InputHintMessage = "Enter — запустить · ↑/↓ — навигация · Esc — закрыть";

    private readonly IReadOnlyList<CommandPaletteItemViewModel> _items;
    private IReadOnlyList<CommandPaletteItemViewModel> _filteredItems;
    private string _searchQuery = string.Empty;
    private int _selectedIndex = -1;
    private bool _isOpen;

    public CommandPaletteViewModel(IEnumerable<CommandPaletteItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items.ToList();
        _filteredItems = _items;

        OpenCommand = new RelayCommand(Open);
        CloseCommand = new RelayCommand(Close);
        ExecuteSelectedCommand = new RelayCommand(ExecuteSelected);
        SelectNextCommand = new RelayCommand(() => MoveSelection(1));
        SelectPreviousCommand = new RelayCommand(() => MoveSelection(-1));
    }

    /// <summary>All registered commands, independent of the current filter.</summary>
    public IReadOnlyList<CommandPaletteItemViewModel> Items => _items;

    /// <summary>Commands matching <see cref="SearchQuery"/>; the whole set while the query is empty.</summary>
    public IReadOnlyList<CommandPaletteItemViewModel> FilteredItems
    {
        get => _filteredItems;
        private set => SetProperty(ref _filteredItems, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool HasResults => _filteredItems.Count > 0;

    public string ResultSummary => HasResults
        ? $"Команд: {_filteredItems.Count}"
        : NoResultsMessage;

    /// <summary>Free-text filter; changing it re-applies the filter and selects the first match.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var normalized = value ?? string.Empty;

            if (SetProperty(ref _searchQuery, normalized))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// Index into <see cref="FilteredItems"/>, or -1 when there is no selection. Out-of-range values
    /// clear the selection instead of throwing, so a shrinking filter can never leave a stale index.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = value < 0 || value >= _filteredItems.Count ? -1 : value;

            if (SetProperty(ref _selectedIndex, clamped))
            {
                OnPropertyChanged(nameof(SelectedItem));
            }
        }
    }

    public CommandPaletteItemViewModel? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _filteredItems.Count
            ? _filteredItems[_selectedIndex]
            : null;

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand ExecuteSelectedCommand { get; }

    public ICommand SelectNextCommand { get; }

    public ICommand SelectPreviousCommand { get; }

    /// <summary>Opens the palette with a cleared query, filtering the full set and selecting the first row.</summary>
    public void Open()
    {
        _searchQuery = string.Empty;
        OnPropertyChanged(nameof(SearchQuery));

        ApplyFilter();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>Runs the selected command and closes the palette. Without a selection this is a no-op.</summary>
    public void ExecuteSelected()
    {
        var item = SelectedItem;

        if (item is null)
        {
            return;
        }

        Close();
        item.Command.Execute(null);
    }

    /// <summary>
    /// Builds the default palette set: navigation to all ten screens (Ctrl+1..Ctrl+0), Dark/Light theme
    /// switching, CLI re-detection, the Health Center, and the workflow studio/monitor/import/export
    /// entries (ТЗ §7.2, ROADMAP Phase 11).
    /// </summary>
    public static CommandPaletteViewModel CreateDefault(
        ICommandPaletteCommandHost host,
        IEnumerable<CommandPaletteItemViewModel>? additionalItems = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var items = new List<CommandPaletteItemViewModel>();

        foreach (var screen in host.Screens)
        {
            var screenId = screen.Id;

            items.Add(new CommandPaletteItemViewModel(
                $"navigation.{screenId}",
                screen.Title,
                screen.Description,
                CommandPaletteCategories.Navigation,
                screen.Shortcut,
                new RelayCommand(() => host.NavigateTo(screenId))));
        }

        items.Add(new CommandPaletteItemViewModel(
            "theme.dark",
            "Переключить на тёмную тему",
            "Применить тёмную палитру к рабочей области.",
            CommandPaletteCategories.Theme,
            string.Empty,
            new RelayCommand(() => host.SetTheme(AppTheme.Dark))));

        items.Add(new CommandPaletteItemViewModel(
            "theme.light",
            "Переключить на светлую тему",
            "Применить светлую палитру к рабочей области.",
            CommandPaletteCategories.Theme,
            string.Empty,
            new RelayCommand(() => host.SetTheme(AppTheme.Light))));

        items.Add(new CommandPaletteItemViewModel(
            "actions.refresh-cli",
            "Обновить статус CLI",
            "Повторно проверить исполняемые файлы OpenCode и Cursor Agent в PATH.",
            CommandPaletteCategories.Actions,
            "F5",
            new RelayCommand(host.RefreshCli)));

        items.Add(new CommandPaletteItemViewModel(
            "actions.health-center",
            "Открыть Центр здоровья",
            "Проверить области предохранителей, зонды восстановления и затронутые сессии.",
            CommandPaletteCategories.Actions,
            "Ctrl+9",
            new RelayCommand(() => host.NavigateTo(ScreenId.HealthCenter))));

        items.Add(new CommandPaletteItemViewModel(
            "workflow.import",
            "Импорт пакета сценария",
            "Импортировать ZIP-пакет процесса в неизменяемую библиотеку.",
            CommandPaletteCategories.Workflow,
            string.Empty,
            new RelayCommand(host.ImportWorkflow)));

        items.Add(new CommandPaletteItemViewModel(
            "workflow.export",
            "Экспорт пакета сценария",
            "Экспортировать выбранную версию процесса побайтово идентично.",
            CommandPaletteCategories.Workflow,
            string.Empty,
            new RelayCommand(host.ExportWorkflow)));

        items.Add(new CommandPaletteItemViewModel(
            "workflow.studio",
            "Открыть Workflow Studio",
            "Редактировать версии шаблонов и утверждать черновики документов до кодирования.",
            CommandPaletteCategories.Workflow,
            string.Empty,
            new RelayCommand(host.OpenWorkflowStudio)));

        items.Add(new CommandPaletteItemViewModel(
            "workflow.activity-monitor",
            "Открыть Activity Monitor",
            "Следить за наблюдаемой цепочкой этапов сценария и активностью ролей.",
            CommandPaletteCategories.Workflow,
            string.Empty,
            new RelayCommand(host.OpenWorkflowActivityMonitor)));

        if (additionalItems is not null)
        {
            items.AddRange(additionalItems);
        }

        return new CommandPaletteViewModel(items);
    }

    private void ApplyFilter()
    {
        var query = _searchQuery.Trim();

        FilteredItems = query.Length == 0
            ? _items
            : _items.Where(item => Matches(item, query)).ToList();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultSummary));

        SelectedIndex = _filteredItems.Count > 0 ? 0 : -1;
    }

    private void MoveSelection(int delta)
    {
        if (_filteredItems.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        if (_selectedIndex < 0)
        {
            SelectedIndex = delta >= 0 ? 0 : _filteredItems.Count - 1;
            return;
        }

        var next = (_selectedIndex + delta) % _filteredItems.Count;

        if (next < 0)
        {
            next += _filteredItems.Count;
        }

        SelectedIndex = next;
    }

    private static bool Matches(CommandPaletteItemViewModel item, string query) =>
        item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.Category.Contains(query, StringComparison.OrdinalIgnoreCase);
}
