using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Backends.CursorAcp;

namespace LLMWorkGUI.App.ViewModels;

public sealed partial class CursorWorkspaceViewModel
{
    private readonly ICursorAcpExecutionJournal? _executionJournal;
    private CursorAcpStoredRoute? _selectedRoute;
    public ObservableCollection<CursorAcpStoredRoute> Routes { get; } = new();
    public ICommand RefreshRoutesCommand { get; }
    public CursorAcpStoredRoute? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (!IsBusy && (value is null || Routes.Contains(value))) SetProperty(ref _selectedRoute, value);
        }
    }

    public async Task RefreshRoutesAsync()
    {
        if (IsDisposed || IsBusy) return;
        IsBusy = true;
        try
        {
            var previous = _selectedRoute;
            var routes = _executionJournal is null ? Array.Empty<CursorAcpStoredRoute>() : await _executionJournal.ListRoutesAsync();
            if (IsDisposed) return;
            Routes.Clear();
            foreach (var route in routes) Routes.Add(route);
            _selectedRoute = routes.FirstOrDefault(route => route == previous);
            OnPropertyChanged(nameof(SelectedRoute));
            Blocker = routes.Count == 0 ? "Нет доступных сохранённых маршрутов Cursor. Настройте профиль, аккаунт и модель." : string.Empty;
        }
        catch (Exception)
        {
            Routes.Clear(); _selectedRoute = null; OnPropertyChanged(nameof(SelectedRoute));
            Blocker = "Не удалось прочитать сохранённые маршруты Cursor.";
        }
        finally { if (!IsDisposed) IsBusy = false; }
    }
}
