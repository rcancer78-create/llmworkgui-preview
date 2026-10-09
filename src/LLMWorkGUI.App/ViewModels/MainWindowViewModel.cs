using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LLMWorkGUI.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    public const string ApplicationTitle = "LLM Work GUI";

    private NavigationItemViewModel _selectedNavigationItem;
    private ScreenViewModel _currentScreen;

    public MainWindowViewModel(
        WorkspaceViewModel workspace,
        ProvidersAccountsViewModel providersAccounts,
        QuotasViewModel quotas,
        HealthCenterViewModel healthCenter,
        SettingsDiagnosticsViewModel settingsDiagnostics,
        StatusBarViewModel statusBar,
        CliStatusViewModel cliStatus,
        ThemeSelectorViewModel themeSelector,
        WorkflowLibraryViewModel? workflowLibrary = null,
        IProjectRepository? projects = null, ISessionRepository? sessions = null,
        IExecutionRepository? executions = null, ISanitizedCatalogProvider? models = null,
        ModelsRoutesViewModel? modelsRoutes = null, IProjectDataPolicyService? projectDataPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(providersAccounts);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(healthCenter);
        ArgumentNullException.ThrowIfNull(settingsDiagnostics);
        ArgumentNullException.ThrowIfNull(statusBar);
        ArgumentNullException.ThrowIfNull(cliStatus);
        ArgumentNullException.ThrowIfNull(themeSelector);

        Screens = ScreenCatalog.CreateScreens(
            workspace,
            providersAccounts,
            quotas,
            healthCenter,
            settingsDiagnostics,
            workflowLibrary, projects, sessions, executions, models, modelsRoutes, projectDataPolicy);

        NavigationItems = new ObservableCollection<NavigationItemViewModel>(
            Screens.Select(screen => new NavigationItemViewModel(screen)));

        StatusBar = statusBar;
        CliStatus = cliStatus;
        ThemeSelector = themeSelector;

        _selectedNavigationItem = NavigationItems[0];
        _selectedNavigationItem.IsSelected = true;
        _currentScreen = _selectedNavigationItem.Screen;

        NavigateCommand = new RelayCommand(parameter => NavigateTo(ResolveScreenId(parameter)));
        RefreshCliDetectionCommand = new RelayCommand(
            () => _ = CliStatus.RefreshAsync(),
            () => !CliStatus.IsDetectionPending);
    }

    public string Title => ApplicationTitle;

    public IReadOnlyList<ScreenViewModel> Screens { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public StatusBarViewModel StatusBar { get; }

    public CliStatusViewModel CliStatus { get; }

    public ThemeSelectorViewModel ThemeSelector { get; }

    public ICommand NavigateCommand { get; }

    public ICommand RefreshCliDetectionCommand { get; }

    public ScreenViewModel CurrentScreen
    {
        get => _currentScreen;
        private set => SetProperty(ref _currentScreen, value);
    }

    public ScreenId CurrentScreenId => CurrentScreen.Id;

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (value is null || !NavigationItems.Contains(value))
            {
                return;
            }

            if (!SetProperty(ref _selectedNavigationItem, value))
            {
                return;
            }

            foreach (var item in NavigationItems)
            {
                item.IsSelected = ReferenceEquals(item, value);
            }

            CurrentScreen = value.Screen;
            if (CurrentScreen is CatalogScreenViewModel catalog) _ = catalog.RefreshAsync();
            if (CurrentScreen is ModelsRoutesViewModel modelRoutes) _ = modelRoutes.RefreshAsync();
            if (CurrentScreen is ProvidersAccountsViewModel providers) _ = providers.InitializeAsync();
            OnPropertyChanged(nameof(CurrentScreenId));
        }
    }

    public void NavigateTo(ScreenId screenId)
    {
        var item = NavigationItems.FirstOrDefault(candidate => candidate.Id == screenId)
            ?? throw new ArgumentOutOfRangeException(
                nameof(screenId),
                screenId,
                "The requested screen is not part of the navigation catalog.");

        SelectedNavigationItem = item;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return CliStatus.RefreshAsync(cancellationToken);
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
}
