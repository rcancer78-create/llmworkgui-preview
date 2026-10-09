using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ProjectDataPolicyViewModel(IProjectDataPolicyService? service, Func<bool>? ownerAvailable = null) : ObservableObject
{
    private ProjectDataPolicySnapshot? _selected;
    private DataClassification _requested = DataClassification.Restricted;
    private bool _confirmed, _busy;
    private string _status = "";
    private ICommand? _save, _refresh;
    public ObservableCollection<ProjectDataPolicySnapshot> Projects { get; } = new();
    public IReadOnlyList<ConfigurationChoice<DataClassification>> Choices { get; } =
        [new(DataClassification.Restricted, "Ограниченные данные"), new(DataClassification.PrivateSource, "Приватные данные"), new(DataClassification.PublicSource, "Открытые данные")];
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool CanEdit => !IsBusy && service is not null && (ownerAvailable?.Invoke() ?? true);
    public ProjectDataPolicySnapshot? SelectedProject
    {
        get => _selected;
        set
        {
            if (IsBusy || value is not null && !Projects.Contains(value) || !SetProperty(ref _selected, value)) return;
            _requested = value?.DataClassification ?? DataClassification.Restricted;
            OnPropertyChanged(nameof(RequestedClass));
            Confirmed = false;
            StatusMessage = "";
            OnPropertyChanged(nameof(CurrentPolicy));
            Changed();
        }
    }
    public string CurrentPolicy => SelectedProject is null ? "Выберите проект."
        : $"Путь: {SelectedProject.RootPath}\nТекущий класс: {Choices.Single(x => x.Value == SelectedProject.DataClassification).Label}";
    public DataClassification RequestedClass
    {
        get => _requested;
        set { if (!IsBusy && SetProperty(ref _requested, value)) { Confirmed = false; Changed(); } }
    }
    public bool Confirmed { get => _confirmed; set { if (!IsBusy && SetProperty(ref _confirmed, value)) Changed(); } }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanSave => CanEdit && SelectedProject is not null && Confirmed && RequestedClass != SelectedProject.DataClassification;
    public ICommand SaveCommand => _save ??= new RelayCommand(() => _ = SaveAsync(), () => CanSave);
    public ICommand RefreshCommand => _refresh ??= new RelayCommand(() => _ = RefreshAsync(), () => CanEdit);
    public async Task RefreshAsync()
    {
        if (IsBusy || service is null) return;
        var id = SelectedProject?.ProjectId;
        IsBusy = true;
        try
        {
            var rows = await service!.ListAsync();
            Projects.Clear(); foreach (var row in rows) Projects.Add(row);
            _selected = Projects.FirstOrDefault(x => x.ProjectId == id);
            _requested = _selected?.DataClassification ?? DataClassification.Restricted;
            _confirmed = false;
            OnPropertyChanged(nameof(SelectedProject));
            OnPropertyChanged(nameof(RequestedClass));
            OnPropertyChanged(nameof(Confirmed));
            OnPropertyChanged(nameof(CurrentPolicy));
            StatusMessage = "";
        }
        catch (InvalidOperationException error) { ClearObservation(); StatusMessage = error.Message; }
        catch (Exception) { ClearObservation(); StatusMessage = "Не удалось прочитать политику проектов. Изменения не разрешены."; }
        finally { IsBusy = false; }
    }
    public async Task SaveAsync()
    {
        if (!CanSave) return;
        var expected = SelectedProject!;
        var requested = RequestedClass;
        IsBusy = true;
        var success = false;
        try { await service!.ChangeAsync(expected, requested, true); success = true; }
        catch (InvalidOperationException error) { ClearObservation(); StatusMessage = error.Message + " Обновите список перед повтором."; }
        catch (Exception) { ClearObservation(); StatusMessage = "Изменение не подтверждено. Обновите список перед повтором."; }
        finally { IsBusy = false; }
        if (success) { await RefreshAsync(); StatusMessage = "Класс данных сохранён. Разрешения внешней передачи проверяются отдельно перед запросом."; }
    }
    private void ClearObservation()
    {
        _selected = null; _confirmed = false;
        OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(Confirmed)); OnPropertyChanged(nameof(CurrentPolicy)); Changed();
    }
    private void Changed()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanEdit)); RelayCommand.RaiseCanExecuteChanged();
    }
}
