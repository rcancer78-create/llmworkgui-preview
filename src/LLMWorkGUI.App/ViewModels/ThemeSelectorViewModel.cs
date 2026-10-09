using LLMWorkGUI.App.Services;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ThemeSelectorViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    public ThemeSelectorViewModel(IThemeService themeService)
    {
        ArgumentNullException.ThrowIfNull(themeService);

        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;

        SetDarkCommand = new RelayCommand(() => _ = SetThemeAsync(AppTheme.Dark));
        SetLightCommand = new RelayCommand(() => _ = SetThemeAsync(AppTheme.Light));
        SetSystemCommand = new RelayCommand(() => _ = SetThemeAsync(AppTheme.System));
    }

    public AppTheme CurrentTheme => _themeService.CurrentTheme;

    public AppTheme EffectiveTheme => _themeService.EffectiveTheme;

    public bool IsDarkSelected => CurrentTheme == AppTheme.Dark;

    public bool IsLightSelected => CurrentTheme == AppTheme.Light;

    public bool IsSystemSelected => CurrentTheme == AppTheme.System;

    public string EffectiveThemeDisplay => $"Текущая тема: {EffectiveTheme switch
    {
        AppTheme.Dark => "Тёмная",
        AppTheme.Light => "Светлая",
        AppTheme.System => "Системная",
        _ => "Неизвестная"
    }}";

    public string? PersistenceWarning => _themeService.LastPersistenceError is null
        ? null
        : "Не удалось сохранить выбранную тему. Проверьте настройки приложения и повторите попытку.";

    public RelayCommand SetDarkCommand { get; }

    public RelayCommand SetLightCommand { get; }

    public RelayCommand SetSystemCommand { get; }

    private async Task SetThemeAsync(AppTheme theme)
    {
        await _themeService.SetThemeAsync(theme).ConfigureAwait(true);

        OnPropertyChanged(nameof(PersistenceWarning));
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(EffectiveTheme));
        OnPropertyChanged(nameof(IsDarkSelected));
        OnPropertyChanged(nameof(IsLightSelected));
        OnPropertyChanged(nameof(IsSystemSelected));
        OnPropertyChanged(nameof(EffectiveThemeDisplay));
    }
}
