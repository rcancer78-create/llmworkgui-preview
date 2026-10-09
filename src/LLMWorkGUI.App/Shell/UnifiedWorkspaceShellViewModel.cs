using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Unified workspace shell (ROADMAP Phase 11): the three-pane ergonomics around the normative screen
/// catalog. It owns the collapsible left navigation pane, the central workspace pane, the right
/// inspector pane, the compact quota/breaker/CLI widgets, the command palette, the global shortcuts and
/// the persisted layout memory. It never replaces <see cref="MainWindowViewModel"/>: it drives the same
/// navigation catalog, so the two stay consistent.
/// </summary>
public sealed class UnifiedWorkspaceShellViewModel : ObservableObject, ICommandPaletteCommandHost
{
    public const double MinLeftPaneWidth = 180;
    public const double MaxLeftPaneWidth = 480;
    public const double MinRightPaneWidth = 220;
    public const double MaxRightPaneWidth = 560;

    private readonly MainWindowViewModel _mainWindow;
    private readonly StatusBarViewModel _statusBar;
    private readonly CliStatusViewModel _cliStatus;
    private readonly ThemeSelectorViewModel _themeSelector;
    private readonly ILayoutPersistenceService? _layoutPersistence;
    private readonly ActivityCenterViewModel? _activityCenter;
    private readonly OnboardingViewModel? _onboarding;
    private readonly WorkflowConsolidatedViewModel? _workflowConsole;

    private bool _isLeftPaneVisible = true;
    private bool _isRightPaneVisible = true;
    private bool _isCompactLayout;
    private bool _compactInspectorOpen;
    public bool IsInspectorDisplayed => _isCompactLayout ? _compactInspectorOpen : IsRightPaneVisible;
    public void SetViewportWidth(double width)
    {
        var compact = width < 1100;
        if (_isCompactLayout == compact) return;
        _isCompactLayout = compact;
        _compactInspectorOpen = false;
        OnPropertyChanged(nameof(IsInspectorDisplayed));
        OnPropertyChanged(nameof(RightPaneGridLength));
    }
    private bool _isActivityCenterOpen;
    private bool _isOnboardingOpen;
    private bool _isWorkflowConsoleOpen;
    private double _leftPaneWidth = ShellLayoutState.DefaultLeftPaneWidth;
    private double _rightPaneWidth = ShellLayoutState.DefaultRightPaneWidth;
    private string _shellNotice = string.Empty;

    public UnifiedWorkspaceShellViewModel(
        MainWindowViewModel mainWindow,
        ILayoutPersistenceService? layoutPersistence = null,
        ActivityCenterViewModel? activityCenter = null,
        OnboardingViewModel? onboarding = null,
        WorkflowConsolidatedViewModel? workflowConsole = null,
        bool autoOpenOnboarding = false)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);

        _mainWindow = mainWindow;
        _statusBar = mainWindow.StatusBar;
        _cliStatus = mainWindow.CliStatus;
        _themeSelector = mainWindow.ThemeSelector;
        _layoutPersistence = layoutPersistence;
        _activityCenter = activityCenter;
        _onboarding = onboarding;
        _workflowConsole = workflowConsole;

        _mainWindow.PropertyChanged += OnMainWindowPropertyChanged;
        _statusBar.PropertyChanged += OnStatusBarPropertyChanged;
        _cliStatus.PropertyChanged += OnCliStatusPropertyChanged;

        if (_onboarding is not null)
        {
            _onboarding.CloseRequested += OnOnboardingCloseRequested;
            _onboarding.WorkspaceRequested += OnOnboardingWorkspaceRequested;
        }

        CommandPalette = CommandPaletteViewModel.CreateDefault(this, CreateShellCommands());

        ToggleLeftPaneCommand = new RelayCommand(ToggleLeftPane);
        ToggleRightPaneCommand = new RelayCommand(ToggleRightPane);
        OpenCommandPaletteCommand = new RelayCommand(OpenCommandPalette);
        CloseCommandPaletteCommand = new RelayCommand(CloseCommandPalette);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        RefreshCliCommand = new RelayCommand(
            () =>
            {
                _ = _cliStatus.RefreshAsync();
                if (ActiveScreen is CatalogScreenViewModel catalog) _ = catalog.RefreshAsync();
                if (ActiveScreen is WorkspaceViewModel workspace) _ = workspace.RefreshOpenCodeRoutesAsync();
            },
            () => !_cliStatus.IsDetectionPending);
        NavigateScreenCommand = new RelayCommand(parameter => NavigateTo(ResolveScreenId(parameter)));
        PersistLayoutCommand = new RelayCommand(PersistLayout);
        OpenActivityCenterCommand = new RelayCommand(OpenActivityCenter);
        CloseActivityCenterCommand = new RelayCommand(CloseActivityCenter);
        OpenOnboardingCommand = new RelayCommand(OpenOnboarding);
        CloseOnboardingCommand = new RelayCommand(CloseOnboarding);
        OpenWorkflowConsoleCommand = new RelayCommand(OpenWorkflowConsole);
        CloseWorkflowConsoleCommand = new RelayCommand(CloseWorkflowConsole);

        if (autoOpenOnboarding && _onboarding is not null)
        {
            _ = TryAutoOpenOnboardingAsync();
        }
    }

    public string Title => MainWindowViewModel.ApplicationTitle;

    /// <summary>The navigation catalog owned by the main window; the shell renders it, never copies it.</summary>
    public IReadOnlyList<ScreenViewModel> Screens => _mainWindow.Screens;

    public System.Collections.ObjectModel.ObservableCollection<NavigationItemViewModel> NavigationItems =>
        _mainWindow.NavigationItems;

    public ScreenViewModel ActiveScreen => _mainWindow.CurrentScreen;

    public ScreenId ActiveScreenId => _mainWindow.CurrentScreenId;

    public string ActiveScreenTitle => ActiveScreen.Title;

    public string ActiveScreenDescription => ActiveScreen.Description;

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _mainWindow.SelectedNavigationItem;
        set
        {
            if (value is null || ReferenceEquals(_mainWindow.SelectedNavigationItem, value))
            {
                return;
            }

            _mainWindow.SelectedNavigationItem = value;
            OnPropertyChanged();
        }
    }

    public StatusBarViewModel StatusBar => _statusBar;

    public CliStatusViewModel CliStatus => _cliStatus;

    public ThemeSelectorViewModel ThemeSelector => _themeSelector;

    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>The Activity Center overlay host; null when the composition did not provide it.</summary>
    public ActivityCenterViewModel? ActivityCenter => _activityCenter;

    public bool IsActivityCenterOpen
    {
        get => _isActivityCenterOpen;
        private set => SetProperty(ref _isActivityCenterOpen, value);
    }

    /// <summary>The onboarding wizard host; null when the composition did not provide it.</summary>
    public OnboardingViewModel? Onboarding => _onboarding;

    /// <summary>The consolidated workflow console host; null when the composition did not provide it.</summary>
    public WorkflowConsolidatedViewModel? WorkflowConsole => _workflowConsole;

    public bool IsOnboardingOpen
    {
        get => _isOnboardingOpen;
        private set => SetProperty(ref _isOnboardingOpen, value);
    }

    public bool IsWorkflowConsoleOpen
    {
        get => _isWorkflowConsoleOpen;
        private set => SetProperty(ref _isWorkflowConsoleOpen, value);
    }

    public bool IsOnboardingAvailable => _onboarding is not null;

    public bool IsWorkflowConsoleAvailable => _workflowConsole is not null;

    public string OnboardingHint => IsOnboardingAvailable
        ? "Онбординг можно открыть в любой момент из справки."
        : "Онбординг не сконфигурирован в этой композиции.";

    public string WorkflowConsoleHint => IsWorkflowConsoleAvailable
        ? "Студия и монитор открываются в единой консоли сценариев."
        : "Workflow Console не сконфигурирован в этой композиции.";

    public ICommand OpenActivityCenterCommand { get; }

    public ICommand CloseActivityCenterCommand { get; }

    public ICommand OpenOnboardingCommand { get; }

    public ICommand CloseOnboardingCommand { get; }

    public ICommand OpenWorkflowConsoleCommand { get; }

    public ICommand CloseWorkflowConsoleCommand { get; }

    public ICommand ToggleLeftPaneCommand { get; }

    public ICommand ToggleRightPaneCommand { get; }

    public ICommand OpenCommandPaletteCommand { get; }

    public ICommand CloseCommandPaletteCommand { get; }

    public ICommand ToggleThemeCommand { get; }

    public ICommand RefreshCliCommand { get; }

    public ICommand NavigateScreenCommand { get; }

    public ICommand PersistLayoutCommand { get; }

    public bool IsLeftPaneVisible
    {
        get => _isLeftPaneVisible;
        private set
        {
            if (!SetProperty(ref _isLeftPaneVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LeftPaneGridLength));
            OnPropertyChanged(nameof(IsFocusMode));
            PersistLayout();
        }
    }

    public bool IsRightPaneVisible
    {
        get => _isRightPaneVisible;
        private set
        {
            if (!SetProperty(ref _isRightPaneVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RightPaneGridLength));
            OnPropertyChanged(nameof(IsInspectorDisplayed));
            OnPropertyChanged(nameof(IsFocusMode));
            PersistLayout();
        }
    }

    public bool IsFocusMode => !IsLeftPaneVisible && !IsRightPaneVisible;

    public double LeftPaneWidth
    {
        get => _leftPaneWidth;
        private set
        {
            if (SetProperty(ref _leftPaneWidth, Clamp(value, MinLeftPaneWidth, MaxLeftPaneWidth)))
            {
                OnPropertyChanged(nameof(LeftPaneGridLength));
            }
        }
    }

    public double RightPaneWidth
    {
        get => _rightPaneWidth;
        private set
        {
            if (SetProperty(ref _rightPaneWidth, Clamp(value, MinRightPaneWidth, MaxRightPaneWidth)))
            {
                OnPropertyChanged(nameof(RightPaneGridLength));
            }
        }
    }

    /// <summary>Column width of the left pane; zero while the pane is collapsed.</summary>
    public GridLength LeftPaneGridLength =>
        IsLeftPaneVisible ? new GridLength(LeftPaneWidth) : new GridLength(0);

    /// <summary>Column width of the right pane; zero while the pane is collapsed.</summary>
    public GridLength RightPaneGridLength =>
        IsInspectorDisplayed ? new GridLength(RightPaneWidth) : new GridLength(0);

    /// <summary>Quota compact widget summary, taken from the shared status bar.</summary>
    public string QuotaSummary => _statusBar.QuotaSummary;

    public string QuotaFreshness => _statusBar.QuotaFreshness;

    /// <summary>Circuit breaker health compact widget, taken from the shared status bar.</summary>
    public string BreakerHealthIndicator => _statusBar.HealthIndicator;

    public string BreakerHealthDetail => _statusBar.HealthDetail;

    /// <summary>CLI detection compact widget, taken from the shared CLI status.</summary>
    public string CliDetectionIndicator => _cliStatus.DetectionSummary;

    public bool IsDegraded => _statusBar.IsDegraded;

    /// <summary>Last palette action feedback, shown in the shell footer. Empty when nothing was run.</summary>
    public string ShellNotice
    {
        get => _shellNotice;
        private set
        {
            if (SetProperty(ref _shellNotice, value))
            {
                OnPropertyChanged(nameof(HasShellNotice));
            }
        }
    }

    public bool HasShellNotice => !string.IsNullOrWhiteSpace(ShellNotice);

    public string PaletteHint => $"Команд: {CommandPalette.Items.Count} · Открыть: Ctrl+K";

    /// <summary>Collapses the left navigation pane or restores it with its last width.</summary>
    public void ToggleLeftPane() => IsLeftPaneVisible = !IsLeftPaneVisible;

    /// <summary>Collapses the right inspector pane or restores it with its last width.</summary>
    public void ToggleRightPane()
    {
        if (!_isCompactLayout) { IsRightPaneVisible = !IsRightPaneVisible; return; }
        _compactInspectorOpen = !_compactInspectorOpen;
        OnPropertyChanged(nameof(IsInspectorDisplayed));
        OnPropertyChanged(nameof(RightPaneGridLength));
    }

    public void OpenCommandPalette()
    {
        ShellNotice = string.Empty;
        IsActivityCenterOpen = false;
        CommandPalette.Open();
    }

    public void CloseCommandPalette()
    {
        CommandPalette.Close();
    }

    /// <summary>Opens the Activity Center overlay over the current screen and refreshes its live query.</summary>
    public void OpenActivityCenter()
    {
        CommandPalette.Close();
        _activityCenter?.Refresh();
        IsActivityCenterOpen = true;
        ShellNotice = "Центр активности открыт.";
    }

    public void CloseActivityCenter()
    {
        IsActivityCenterOpen = false;
        ShellNotice = string.Empty;
    }

    /// <summary>
    /// Opens the onboarding wizard without blocking the shell. When the wizard is not composed the shell
    /// reports it instead of showing an empty overlay.
    /// </summary>
    public void OpenOnboarding()
    {
        if (_onboarding is null)
        {
            ShellNotice = OnboardingHint;
            return;
        }

        CommandPalette.Close();
        IsActivityCenterOpen = false;
        IsWorkflowConsoleOpen = false;
        _ = InitializeOnboardingAsync();
        IsOnboardingOpen = true;
        ShellNotice = "Онбординг открыт; его можно скрыть в любой момент.";
    }

    public void CloseOnboarding()
    {
        IsOnboardingOpen = false;
    }

    /// <summary>Opens the consolidated studio/monitor console over the current screen.</summary>
    public void OpenWorkflowConsole()
    {
        if (_workflowConsole is null)
        {
            ShellNotice = WorkflowConsoleHint;
            return;
        }

        CommandPalette.Close();
        IsActivityCenterOpen = false;
        IsOnboardingOpen = false;
        IsWorkflowConsoleOpen = true;
        ShellNotice = "Консоль сценариев открыта.";
    }

    public void CloseWorkflowConsole()
    {
        IsWorkflowConsoleOpen = false;
        ShellNotice = string.Empty;
    }

    /// <summary>
    /// Initializes the wizard and opens it on the first run (when completion was never persisted). Public
    /// and awaitable so tests can drive the first-run behavior deterministically.
    /// </summary>
    public async Task<bool> TryAutoOpenOnboardingAsync()
    {
        if (_onboarding is null)
        {
            return false;
        }

        await _onboarding.InitializeAsync().ConfigureAwait(true);

        if (_onboarding.IsCompleted)
        {
            return false;
        }

        IsOnboardingOpen = true;
        return true;
    }

    private async Task InitializeOnboardingAsync()
    {
        if (_onboarding is not null)
        {
            await _onboarding.InitializeAsync().ConfigureAwait(true);
        }
    }

    private void OnOnboardingCloseRequested(object? sender, EventArgs e) => CloseOnboarding();

    private void OnOnboardingWorkspaceRequested(object? sender, EventArgs e)
    {
        CloseOnboarding();
        NavigateTo(ScreenId.Workspace);
    }

    /// <summary>Switches between the Dark and Light themes; System keeps its meaning elsewhere.</summary>
    public void ToggleTheme()
    {
        var target = _themeSelector.EffectiveTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

        SetTheme(target);
    }

    /// <summary>Records the actual pane widths after an interactive splitter drag and persists them.</summary>
    public void SetPaneWidths(double leftWidth, double rightWidth)
    {
        LeftPaneWidth = leftWidth;
        RightPaneWidth = rightWidth;
        PersistLayout();
    }

    public void NavigateTo(ScreenId screenId)
    {
        _mainWindow.NavigateTo(screenId);
        ShellNotice = $"Открыт экран: {ActiveScreenTitle}.";
    }

    /// <summary>
    /// Restores widths, collapse state, active screen and theme from the persistence service. Missing
    /// or invalid values keep their defaults; without a service this is a no-op.
    /// </summary>
    public void ApplyPersistedLayout()
    {
        if (_layoutPersistence is null)
        {
            return;
        }

        var state = _layoutPersistence.Load();

        LeftPaneWidth = Normalize(state.LeftPaneWidth, ShellLayoutState.DefaultLeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
        RightPaneWidth = Normalize(state.RightPaneWidth, ShellLayoutState.DefaultRightPaneWidth, MinRightPaneWidth, MaxRightPaneWidth);
        IsLeftPaneVisible = state.IsLeftPaneVisible;
        IsRightPaneVisible = state.IsRightPaneVisible;

        if (Enum.TryParse<ScreenId>(state.ActiveScreen, ignoreCase: true, out var screenId) &&
            _mainWindow.NavigationItems.Any(item => item.Id == screenId))
        {
            NavigateTo(screenId);
        }

        if (Enum.TryParse<AppTheme>(state.Theme, ignoreCase: true, out var theme) && Enum.IsDefined(theme))
        {
            SetTheme(theme);
        }
    }

    /// <summary>Snapshot of the current shell geometry and settings for persistence.</summary>
    public ShellLayoutState CreateLayoutState() => new()
    {
        LeftPaneWidth = LeftPaneWidth,
        RightPaneWidth = RightPaneWidth,
        IsLeftPaneVisible = IsLeftPaneVisible,
        IsRightPaneVisible = IsRightPaneVisible,
        ActiveScreen = ActiveScreenId.ToString(),
        Theme = _themeSelector.CurrentTheme.ToString()
    };

    public void PersistLayout()
    {
        _layoutPersistence?.Save(CreateLayoutState());
    }

    public void RefreshCli()
    {
        _ = _cliStatus.RefreshAsync();
        ShellNotice = "Сведения об обнаружении CLI обновлены.";
    }

    public void OpenWorkflowStudio()
    {
        OpenWorkflowConsole();
        _workflowConsole?.SelectStudioPanel(WorkflowStudioPanelMode.Templates);

        if (_workflowConsole is not null)
        {
            ShellNotice = "Студия сценариев открыта в консоли сценариев.";
        }
    }

    public void OpenWorkflowActivityMonitor()
    {
        OpenWorkflowConsole();
        _workflowConsole?.SelectMonitorPanel(WorkflowMonitorPanelMode.Schema);

        if (_workflowConsole is not null)
        {
            ShellNotice = "Монитор активности открыт в консоли сценариев.";
        }
    }

    public void ImportWorkflow()
    {
        OpenWorkflowConsole();

        if (_workflowConsole?.Library is { } library)
        {
            if (library.CanQuickImport)
            {
                _ = library.QuickImportAsync();
            }
            else
            {
                ShellNotice = "Укажите путь к ZIP-пакету в Workflow Console для быстрого импорта.";
            }
        }
    }

    public void ExportWorkflow()
    {
        OpenWorkflowConsole();

        if (_workflowConsole?.Library is { } library)
        {
            if (library.CanQuickExport)
            {
                _ = library.QuickExportAsync();
            }
            else
            {
                ShellNotice = "Выберите версию сценария в консоли сценариев для быстрого экспорта.";
            }
        }
    }

    public void SetTheme(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.Dark:
                _themeSelector.SetDarkCommand.Execute(null);
                break;
            case AppTheme.Light:
                _themeSelector.SetLightCommand.Execute(null);
                break;
            case AppTheme.System:
                _themeSelector.SetSystemCommand.Execute(null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unsupported theme.");
        }

        var themeName = theme switch
        {
            AppTheme.Dark => "Тёмная",
            AppTheme.Light => "Светлая",
            AppTheme.System => "Системная",
            _ => theme.ToString()
        };
        ShellNotice = $"Тема изменена на: {themeName}.";
        PersistLayout();
    }

    private IEnumerable<CommandPaletteItemViewModel> CreateShellCommands()
    {
        var items = new List<CommandPaletteItemViewModel>
        {
            new(
                "help.onboarding",
                "Открыть онбординг",
                "Первый запуск без API-ключей и платных вызовов: рабочая область, локальные CLI, каталог шаблонов.",
                CommandPaletteCategories.Actions,
                string.Empty,
                new RelayCommand(OpenOnboarding)),
            new(
                "workflow.console",
                "Открыть консоль сценариев",
                "Единый компактный переключатель шаблонов, ролей, документов, схемы запуска, активности и инспектора.",
                CommandPaletteCategories.Workflow,
                string.Empty,
                new RelayCommand(OpenWorkflowConsole))
        };

        if (_activityCenter is not null)
        {
            items.Add(new CommandPaletteItemViewModel(
                "actions.activity-center",
                "Открыть центр активности",
                "Поиск и фильтрация общего журнала запусков, сеансов, изменений состояния и действий пользователя.",
                CommandPaletteCategories.Actions,
                string.Empty,
                new RelayCommand(OpenActivityCenter)));
        }

        return items;
    }

    private void OnMainWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.CurrentScreen):
            case nameof(MainWindowViewModel.CurrentScreenId):
                OnPropertyChanged(nameof(ActiveScreen));
                OnPropertyChanged(nameof(ActiveScreenId));
                OnPropertyChanged(nameof(ActiveScreenTitle));
                OnPropertyChanged(nameof(ActiveScreenDescription));
                break;
            case nameof(MainWindowViewModel.SelectedNavigationItem):
                OnPropertyChanged(nameof(SelectedNavigationItem));
                break;
            default:
                break;
        }
    }

    private void OnStatusBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StatusBarViewModel.QuotaSummary):
            case nameof(StatusBarViewModel.QuotaFreshness):
                OnPropertyChanged(nameof(QuotaSummary));
                OnPropertyChanged(nameof(QuotaFreshness));
                break;
            case nameof(StatusBarViewModel.HealthIndicator):
            case nameof(StatusBarViewModel.HealthDetail):
                OnPropertyChanged(nameof(BreakerHealthIndicator));
                OnPropertyChanged(nameof(BreakerHealthDetail));
                break;
            case nameof(StatusBarViewModel.IsDegraded):
                OnPropertyChanged(nameof(IsDegraded));
                break;
            default:
                break;
        }
    }

    private void OnCliStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CliStatusViewModel.DetectionSummary):
                OnPropertyChanged(nameof(CliDetectionIndicator));
                break;
            case nameof(CliStatusViewModel.IsDetectionPending):
                RelayCommand.RaiseCanExecuteChanged();
                break;
            default:
                break;
        }
    }

    private static ScreenId ResolveScreenId(object? parameter)
    {
        return parameter switch
        {
            ScreenId screenId => screenId,
            NavigationItemViewModel navigationItem => navigationItem.Id,
            string text when Enum.TryParse(text, ignoreCase: true, out ScreenId parsed) => parsed,
            _ => throw new ArgumentException(
                "Navigation requires a ScreenId, a navigation item, or a screen name.",
                nameof(parameter))
        };
    }

    private static double Normalize(double value, double fallback, double minimum, double maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum)
        {
            return fallback;
        }

        return Clamp(value, minimum, maximum);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return minimum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }
}
