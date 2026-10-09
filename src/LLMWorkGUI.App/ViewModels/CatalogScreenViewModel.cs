using LLMWorkGUI.Application.Projects;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class CatalogScreenViewModel : ScreenViewModel
{
    private readonly IProjectRepository? _projects;
    private readonly ISessionRepository? _sessions;
    private readonly IExecutionRepository? _executions;
    private readonly ISanitizedCatalogProvider? _models;
    private readonly List<CatalogEntry> _allEntries = new();
    private bool _isBusy;
    private bool _loadFailed;
    private string _searchText = string.Empty;
    private string _projectPath = string.Empty;
    private string _statusMessage = string.Empty;
    private readonly string _emptyStateMessage;

    public CatalogScreenViewModel(
        ScreenId id,
        string title,
        string shortcut,
        string description,
        string emptyStateMessage,
        string availabilityNote,
        IProjectRepository? projects = null,
        ISessionRepository? sessions = null,
        IExecutionRepository? executions = null,
        ISanitizedCatalogProvider? models = null, IProjectDataPolicyService? projectDataPolicy = null)
        : base(id, title, shortcut, description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyStateMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(availabilityNote);

        _emptyStateMessage = emptyStateMessage;
        AvailabilityNote = availabilityNote;
        _projects = projects;
        _sessions = sessions;
        _executions = executions;
        _models = models;
        ProjectDataPolicy = new(id == ScreenId.Projects ? projectDataPolicy : null, () => !IsBusy);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy && !ProjectDataPolicy.IsBusy);
        AddProjectCommand = new RelayCommand(() => _ = AddProjectAsync(),
            () => IsProjects && _projects is not null && !IsBusy && !ProjectDataPolicy.IsBusy && !string.IsNullOrWhiteSpace(ProjectPath));
    }

    public string EmptyStateMessage => _loadFailed
        ? "Содержимое каталога не подтверждено. Повторите обновление."
        : _allEntries.Count > 0 && IsEmpty
        ? "По вашему запросу ничего не найдено."
        : _emptyStateMessage;

    public string AvailabilityNote { get; }
    public ProjectDataPolicyViewModel ProjectDataPolicy { get; }

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand AddProjectCommand { get; }
    public bool IsProjects => Id == ScreenId.Projects;
    public bool IsEmpty => Entries.Count == 0;
    public string CountDisplay => _loadFailed
        ? "Количество записей неизвестно."
        : $"Показано: {Entries.Count} из {_allEntries.Count}";
    public bool IsBusy
    {
        get => _isBusy;
        private set { SetProperty(ref _isBusy, value); RelayCommand.RaiseCanExecuteChanged(); }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ProjectPath
    {
        get => _projectPath;
        set { SetProperty(ref _projectPath, value); RelayCommand.RaiseCanExecuteChanged(); }
    }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy || ProjectDataPolicy.IsBusy) return;
        IsBusy = true;
        StatusMessage = "Загрузка…";
        try
        {
            var rows = await LoadEntriesAsync();
            if (IsProjects) await ProjectDataPolicy.RefreshAsync();
            _allEntries.Clear();
            _allEntries.AddRange(rows);
            _loadFailed = false;
            ApplyFilter();
            StatusMessage = string.Empty;
        }
        catch (InvalidOperationException)
        {
            _loadFailed = true;
            ClearEntries();
            StatusMessage = "Хранилище недоступно. Проверьте подключение в настройках и повторите обновление.";
        }
        catch (Exception)
        {
            _loadFailed = true;
            ClearEntries();
            StatusMessage = "Не удалось загрузить каталог. Повторите обновление или откройте диагностику.";
        }
        finally { IsBusy = false; }
    }

    public async Task AddProjectAsync()
    {
        if (IsBusy || ProjectDataPolicy.IsBusy || !IsProjects || _projects is null) return;
        IsBusy = true;
        var projectAdded = false;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectPath.Trim()));
            if (!Directory.Exists(path))
            {
                StatusMessage = "Папка не найдена. Укажите существующую папку проекта.";
                return;
            }
            if ((await _projects.ListAsync()).Any(project =>
                string.Equals(Path.TrimEndingDirectorySeparator(project.RootPath), path, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = "Эта папка уже добавлена в каталог.";
                return;
            }
            // A newly registered folder must not implicitly authorize sending its contents externally.
            var project = new Project(Guid.NewGuid().ToString("N"), new DirectoryInfo(path).Name,
                path, null, false, File.Exists(Path.Combine(path, "AGENTS.md")), null, null,
                DataClassification.Restricted);
            await _projects.UpsertAsync(project);
            projectAdded = true;
            ProjectPath = string.Empty;
            var entries = await LoadEntriesAsync();
            _allEntries.Clear();
            _allEntries.AddRange(entries);
            _loadFailed = false;
            ApplyFilter();
            StatusMessage = "Проект добавлен. Класс данных: ограниченный; внешняя отправка требует разрешения.";
        }
        catch (Exception) when (projectAdded)
        {
            _loadFailed = true;
            ClearEntries();
            StatusMessage = "Проект добавлен, но не удалось обновить каталог. Повторите обновление.";
        }
        catch (Exception)
        {
            StatusMessage = "Не удалось добавить проект. Проверьте путь и доступность хранилища.";
        }
        finally { IsBusy = false; }
    }

    private async Task<IReadOnlyList<CatalogEntry>> LoadEntriesAsync()
    {
        if (Id == ScreenId.Models)
        {
            if (_models is null) throw new InvalidOperationException();
            var catalog = await _models.GetSanitizedCatalogAsync();
            return catalog.Models.SelectMany(model => model.SelectableBackendModelIds.Select(modelId =>
                new CatalogEntry(modelId, modelId,
                    model.IsRoutable ? "Маршрут доступен" : "Маршрут недоступен",
                    $"{model.DisplayName} · {model.Backend} · {model.ProviderProfileId}",
                    model.ContextWindow is int context ? $"Контекст: {context:N0}" : "Контекст не сообщён")))
                .OrderBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        if (_projects is null) throw new InvalidOperationException();
        var projects = await _projects.ListAsync();
        if (IsProjects)
            return projects.OrderBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(project => new CatalogEntry(project.Id, project.DisplayName,
                    project.DataClassification switch { DataClassification.PublicSource => "Открытые данные", DataClassification.PrivateSource => "Приватные данные", _ => "Ограниченные данные" },
                    project.RootPath, project.GitBranch is null ? "Ветка не сообщена" : $"Ветка: {project.GitBranch}"))
                .ToArray();
        if (_sessions is null) throw new InvalidOperationException();
        var sessions = new List<(Project Project, Session Session)>();
        foreach (var project in projects)
            sessions.AddRange((await _sessions.ListByProjectAsync(project.Id)).Select(session => (project, session)));
        if (Id == ScreenId.Sessions)
            return sessions.OrderByDescending(row => row.Session.LastEventAt).Select(row =>
                new CatalogEntry(row.Session.Id, row.Project.DisplayName,
                    SessionStateText(row.Session.State), row.Session.Binding.ModelId,
                    $"{row.Session.LastEventAt.LocalDateTime:g} · {row.Session.NativeSessionId ?? "Нативная сессия не подтверждена"}"))
                .ToArray();
        if (Id != ScreenId.Runs || _executions is null) throw new InvalidOperationException();
        var executions = new List<(string Project, Execution Execution)>();
        foreach (var row in sessions)
            executions.AddRange((await _executions.ListBySessionAsync(row.Session.Id)).Select(execution => (row.Project.DisplayName, execution)));
        return executions.OrderByDescending(row => row.Execution.CreatedAt).Select(row =>
            new CatalogEntry(row.Execution.Id, row.Project, ExecutionStateText(row.Execution.State),
                row.Execution.ObservedRouteId ?? "Маршрут ещё не подтверждён",
                $"{row.Execution.CreatedAt.LocalDateTime:g} · Артефактов: {row.Execution.Artifacts.Count}"))
            .ToArray();
    }

    private void ClearEntries() { _allEntries.Clear(); ApplyFilter(); }
    private void ApplyFilter()
    {
        Entries.Clear();
        var query = SearchText.Trim();
        foreach (var row in _allEntries.Where(row => string.IsNullOrEmpty(query) ||
            $"{row.Id} {row.Title} {row.Status} {row.Description} {row.Detail}".Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            Entries.Add(row);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountDisplay));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private static string SessionStateText(SessionState state) => state switch
    {
        SessionState.Draft => "Черновик", SessionState.Starting => "Запускается",
        SessionState.Active => "Активна", SessionState.Idle => "Ожидание",
        SessionState.Ambiguous => "Требует проверки", SessionState.Orphaned => "Связь потеряна",
        SessionState.Closed => "Закрыта", _ => "Неизвестно"
    };
    private static string ExecutionStateText(ExecutionState state) => state switch
    {
        ExecutionState.Queued => "В очереди", ExecutionState.Starting => "Запускается",
        ExecutionState.SessionConfirmed => "Сессия подтверждена", ExecutionState.Running => "Выполняется",
        ExecutionState.WaitingApproval => "Ожидает подтверждения", ExecutionState.Cancelling => "Отменяется",
        ExecutionState.Succeeded => "Завершён", ExecutionState.Failed => "Ошибка",
        ExecutionState.TimedOut => "Время истекло", ExecutionState.Cancelled => "Отменён",
        ExecutionState.RouteMismatch => "Маршрут не совпадает", _ => "Требует проверки"
    };
}

public sealed record CatalogEntry(string Id, string Title, string Status, string Description, string Detail);
