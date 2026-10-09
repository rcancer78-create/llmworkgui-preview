namespace LLMWorkGUI.App.Services;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }

    AppTheme EffectiveTheme { get; }

    string? LastPersistenceError { get; }

    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default);
}
