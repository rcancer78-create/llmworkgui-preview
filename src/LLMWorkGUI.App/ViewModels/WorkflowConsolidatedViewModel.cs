using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Consolidated Workflow Console (ROADMAP Phase 11): one compact switcher over the Workflow Studio
/// surfaces (template editing, role matrix, document preview) and the Activity Monitor surfaces (run
/// graph schema, activity stream, inspector). It also hosts the one-click workflow package import/export
/// and reuses the same <see cref="WorkflowLibraryViewModel"/> instance as the Workflows screen, so no
/// library state is duplicated.
/// </summary>
public sealed class WorkflowConsolidatedViewModel : ObservableObject
{
    public const string NotReportedPlaceholder = "Not reported";
    public const string UnavailableMessage =
        "Консоль сценариев не сконфигурирована: библиотека сценариев недоступна в этой композиции.";

    private readonly WorkflowLibraryViewModel? _library;

    private WorkflowConsoleMode _activeMode = WorkflowConsoleMode.StudioTemplates;

    public WorkflowConsolidatedViewModel(WorkflowLibraryViewModel? library = null)
    {
        _library = library;

        SelectModeCommand = new RelayCommand(parameter => SelectMode(ResolveMode(parameter)));
        ShowStudioCommand = new RelayCommand(() => SelectMode(WorkflowConsoleMode.StudioTemplates));
        ShowMonitorCommand = new RelayCommand(() => SelectMode(WorkflowConsoleMode.MonitorSchema));

        if (_library is not null)
        {
            // The header shows the run the library actually observed, so the console follows the
            // project's observed state instead of the value captured when a surface was opened.
            _library.PropertyChanged += OnLibraryPropertyChanged;
            SynchronizePanelModes();
        }
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowLibraryViewModel.ObservedRun)
            or nameof(WorkflowLibraryViewModel.ObservedRunDisplay)
            or nameof(WorkflowLibraryViewModel.SelectedVersion))
        {
            OnPropertyChanged(nameof(RunSummaryDisplay));
            OnPropertyChanged(nameof(SelectedWorkflowDisplay));
        }
    }

    public string Title => "Workflow Console";

    public string Description =>
        "Единая компактная связка: шаблоны и матрица ролей студии, схема текущего запуска и инспектор, "
        + "быстрый импорт и экспорт пакетов сценариев.";

    /// <summary>The shared workflow library screen view model; null when it was not composed.</summary>
    public WorkflowLibraryViewModel? Library => _library;

    public WorkflowStudioViewModel? Studio => _library?.Studio;

    public WorkflowActivityMonitorViewModel? Monitor => _library?.ActivityMonitor;

    public bool IsAvailable => _library is not null;

    public string AvailabilityDisplay => IsAvailable ? "Workflow Console готов" : UnavailableMessage;

    public ICommand SelectModeCommand { get; }

    public ICommand ShowStudioCommand { get; }

    public ICommand ShowMonitorCommand { get; }

    /// <summary>The compact surface currently shown.</summary>
    public WorkflowConsoleMode ActiveMode
    {
        get => _activeMode;
        private set
        {
            if (!SetProperty(ref _activeMode, value))
            {
                return;
            }

            SynchronizePanelModes();

            OnPropertyChanged(nameof(IsStudioMode));
            OnPropertyChanged(nameof(IsMonitorMode));
            OnPropertyChanged(nameof(IsStudioVisible));
            OnPropertyChanged(nameof(IsMonitorVisible));
            OnPropertyChanged(nameof(IsTemplatesMode));
            OnPropertyChanged(nameof(IsRoleMatrixMode));
            OnPropertyChanged(nameof(IsDocumentsMode));
            OnPropertyChanged(nameof(IsSchemaMode));
            OnPropertyChanged(nameof(IsActivityMode));
            OnPropertyChanged(nameof(IsInspectorMode));
            OnPropertyChanged(nameof(ActiveModeDisplay));
            OnPropertyChanged(nameof(SelectedWorkflowDisplay));
            OnPropertyChanged(nameof(RunSummaryDisplay));
        }
    }

    public bool IsStudioMode => ActiveMode
        is WorkflowConsoleMode.StudioTemplates
        or WorkflowConsoleMode.StudioRoles
        or WorkflowConsoleMode.StudioDocuments;

    public bool IsMonitorMode => !IsStudioMode;

    public bool IsStudioVisible => IsStudioMode;

    public bool IsMonitorVisible => IsMonitorMode;

    public bool IsTemplatesMode => ActiveMode == WorkflowConsoleMode.StudioTemplates;

    public bool IsRoleMatrixMode => ActiveMode == WorkflowConsoleMode.StudioRoles;

    public bool IsDocumentsMode => ActiveMode == WorkflowConsoleMode.StudioDocuments;

    public bool IsSchemaMode => ActiveMode == WorkflowConsoleMode.MonitorSchema;

    public bool IsActivityMode => ActiveMode == WorkflowConsoleMode.MonitorActivity;

    public bool IsInspectorMode => ActiveMode == WorkflowConsoleMode.MonitorInspector;

    public string ActiveModeDisplay => ActiveMode switch
    {
        WorkflowConsoleMode.StudioRoles => "Студия · Матрица ролей",
        WorkflowConsoleMode.StudioDocuments => "Студия · Предпросмотр документов",
        WorkflowConsoleMode.MonitorSchema => "Монитор · Схема графа",
        WorkflowConsoleMode.MonitorActivity => "Монитор · Поток активности",
        WorkflowConsoleMode.MonitorInspector => "Монитор · Инспектор",
        _ => "Студия · Редактирование шаблонов"
    };

    public string SelectedWorkflowDisplay
    {
        get
        {
            if (_library?.SelectedVersion is { } version)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{version.Version.VersionNumber} · {version.Version.Id}");
            }

            return NotReportedPlaceholder;
        }
    }

    public string RunSummaryDisplay => _library?.ActivityMonitor.RunSummaryDisplay ?? NotReportedPlaceholder;

    /// <summary>Switches the console surface and mirrors it onto the embedded studio/monitor panels.</summary>
    public void SelectMode(WorkflowConsoleMode mode)
    {
        ActiveMode = mode;
    }

    public void SelectStudioPanel(WorkflowStudioPanelMode mode) => SelectMode(mode switch
    {
        WorkflowStudioPanelMode.RoleMatrix => WorkflowConsoleMode.StudioRoles,
        WorkflowStudioPanelMode.Documents => WorkflowConsoleMode.StudioDocuments,
        WorkflowStudioPanelMode.Templates => WorkflowConsoleMode.StudioTemplates,
        _ => WorkflowConsoleMode.StudioTemplates
    });

    public void SelectMonitorPanel(WorkflowMonitorPanelMode mode) => SelectMode(mode switch
    {
        WorkflowMonitorPanelMode.Activity => WorkflowConsoleMode.MonitorActivity,
        WorkflowMonitorPanelMode.Inspector => WorkflowConsoleMode.MonitorInspector,
        _ => WorkflowConsoleMode.MonitorSchema
    });

    public Task<string?> QuickExportAsync() =>
        _library is null ? Task.FromResult<string?>(null) : _library.QuickExportAsync();

    public Task<WorkflowImportResult?> QuickImportAsync() =>
        _library is null
            ? Task.FromResult<WorkflowImportResult?>(null)
            : _library.QuickImportAsync();

    private void SynchronizePanelModes()
    {
        if (_library is null)
        {
            return;
        }

        switch (ActiveMode)
        {
            case WorkflowConsoleMode.StudioTemplates:
                _library.Studio.SelectPanelMode(WorkflowStudioPanelMode.Templates);
                break;
            case WorkflowConsoleMode.StudioRoles:
                _library.Studio.SelectPanelMode(WorkflowStudioPanelMode.RoleMatrix);
                break;
            case WorkflowConsoleMode.StudioDocuments:
                _library.Studio.SelectPanelMode(WorkflowStudioPanelMode.Documents);
                break;
            case WorkflowConsoleMode.MonitorSchema:
                _library.ActivityMonitor.SelectPanelMode(WorkflowMonitorPanelMode.Schema);
                break;
            case WorkflowConsoleMode.MonitorActivity:
                _library.ActivityMonitor.SelectPanelMode(WorkflowMonitorPanelMode.Activity);
                break;
            case WorkflowConsoleMode.MonitorInspector:
                _library.ActivityMonitor.SelectPanelMode(WorkflowMonitorPanelMode.Inspector);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ActiveMode),
                    ActiveMode,
                    "Unsupported workflow console mode.");
        }
    }

    private static WorkflowConsoleMode ResolveMode(object? parameter) =>
        parameter switch
        {
            WorkflowConsoleMode mode => mode,
            WorkflowStudioPanelMode studioMode => studioMode switch
            {
                WorkflowStudioPanelMode.RoleMatrix => WorkflowConsoleMode.StudioRoles,
                WorkflowStudioPanelMode.Documents => WorkflowConsoleMode.StudioDocuments,
                _ => WorkflowConsoleMode.StudioTemplates
            },
            WorkflowMonitorPanelMode monitorMode => monitorMode switch
            {
                WorkflowMonitorPanelMode.Activity => WorkflowConsoleMode.MonitorActivity,
                WorkflowMonitorPanelMode.Inspector => WorkflowConsoleMode.MonitorInspector,
                _ => WorkflowConsoleMode.MonitorSchema
            },
            string text when Enum.TryParse<WorkflowConsoleMode>(text, ignoreCase: true, out var parsed) =>
                parsed,
            _ => throw new ArgumentException(
                "Console navigation requires a WorkflowConsoleMode value.",
                nameof(parameter))
        };
}

/// <summary>The compact surfaces of the consolidated Workflow Console (ROADMAP Phase 11).</summary>
public enum WorkflowConsoleMode
{
    StudioTemplates,
    StudioRoles,
    StudioDocuments,
    MonitorSchema,
    MonitorActivity,
    MonitorInspector
}
