using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Backends.OpenCode.Sessions;

namespace LLMWorkGUI.App.ViewModels;

public sealed record OpenCodeWorkspaceRoute(string Id, string ProviderProfileId, string AccountId, string ModelId, string NativeModelId)
{
    public string Display => $"{NativeModelId} · {AccountId} · {Id}";
    public OpenCodeStoredRoute Stored => new(Id, ProviderProfileId, AccountId, ModelId, NativeModelId);
}

public sealed partial class WorkspaceViewModel
{
    private readonly IOpenCodeExecutionJournal? _executionJournal;
    private OpenCodeWorkspaceRoute? _selectedOpenCodeRoute;
    private string? _sessionDirectory;
    private string? _sessionProjectId;
    private OpenCodeStoredRoute? _sessionRoute;

    public ICommand RefreshOpenCodeRoutesCommand { get; }
    public ObservableCollection<OpenCodeWorkspaceRoute> OpenCodeRoutes { get; } = new();
    public bool CanChooseOpenCodeRoute => !IsBusy && !IsProcessStarted;

    public OpenCodeWorkspaceRoute? SelectedOpenCodeRoute
    {
        get => _selectedOpenCodeRoute;
        set
        {
            if (!CanChooseOpenCodeRoute || (value is not null && !OpenCodeRoutes.Contains(value))) return;
            if (SetProperty(ref _selectedOpenCodeRoute, value)) RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshOpenCodeRoutesAsync()
    {
        if (!CanChooseOpenCodeRoute) return;
        IsBusy = true;
        try
        {
            var routes = _executionJournal is null ? Array.Empty<OpenCodeWorkspaceRoute>()
                : (await _executionJournal.ListRoutesAsync()).Select(route => new OpenCodeWorkspaceRoute(
                    route.Id, route.ProviderProfileId, route.AccountId, route.ModelId, route.NativeModelId)).ToArray();
            var selected = _selectedOpenCodeRoute;
            OpenCodeRoutes.Clear();
            foreach (var route in routes) OpenCodeRoutes.Add(route);
            _selectedOpenCodeRoute = routes.FirstOrDefault(route => route == selected);
            OnPropertyChanged(nameof(SelectedOpenCodeRoute));
            SendBlocker = routes.Length == 0
                ? "Нет доступных сохранённых маршрутов OpenCode. Настройте профиль, аккаунт и модель."
                : string.Empty;
        }
        catch (Exception)
        {
            OpenCodeRoutes.Clear();
            _selectedOpenCodeRoute = null;
            OnPropertyChanged(nameof(SelectedOpenCodeRoute));
            SendBlocker = "Не удалось прочитать сохранённые маршруты OpenCode. Проверьте настройки провайдера.";
        }
        finally { IsBusy = false; }
    }

}
