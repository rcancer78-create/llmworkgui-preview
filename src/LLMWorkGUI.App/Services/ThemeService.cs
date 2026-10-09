using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.App.Services;

public sealed class ThemeService : IThemeService
{
    public const string ThemeSettingKey = "appearance.theme";

    private readonly IApplicationSettingsRepository _settingsRepository;
    private readonly ISystemThemeProvider _systemThemeProvider;
    private readonly IThemeResourceApplier _resourceApplier;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private long _themeRequest;

    private AppTheme _currentTheme = AppTheme.System;
    private AppTheme _effectiveTheme = AppTheme.Light;

    public ThemeService(
        IApplicationSettingsRepository settingsRepository,
        ISystemThemeProvider systemThemeProvider,
        IThemeResourceApplier resourceApplier)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(systemThemeProvider);
        ArgumentNullException.ThrowIfNull(resourceApplier);

        _settingsRepository = settingsRepository;
        _systemThemeProvider = systemThemeProvider;
        _resourceApplier = resourceApplier;
    }

    public AppTheme CurrentTheme => _currentTheme;

    public AppTheme EffectiveTheme => _effectiveTheme;

    public string? LastPersistenceError { get; private set; }

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AppTheme storedTheme;

        try
        {
            var storedValue = await _settingsRepository
                .GetValueAsync(ThemeSettingKey, cancellationToken)
                .ConfigureAwait(true);

            LastPersistenceError = null;
            storedTheme = TryParseTheme(storedValue, out var parsed) ? parsed : AppTheme.System;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastPersistenceError = UiErrorMessage.Describe(exception);
            storedTheme = AppTheme.System;
        }

        ApplyThemeCore(storedTheme, raiseEvent: false);
    }

    public async Task SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        var request = Interlocked.Increment(ref _themeRequest);
        ApplyThemeCore(theme, raiseEvent: true);

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await _settingsRepository
                .SetValueAsync(ThemeSettingKey, theme.ToString(), "String", cancellationToken)
                .ConfigureAwait(true);

            if (request == Interlocked.Read(ref _themeRequest)) LastPersistenceError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (request == Interlocked.Read(ref _themeRequest)) LastPersistenceError = UiErrorMessage.Describe(exception);
        }
        finally { _persistenceGate.Release(); }
    }

    public static bool TryParseTheme(string? value, out AppTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out AppTheme parsed)
            && Enum.IsDefined(parsed))
        {
            theme = parsed;
            return true;
        }

        theme = AppTheme.System;
        return false;
    }

    private void ApplyThemeCore(AppTheme requestedTheme, bool raiseEvent)
    {
        var effectiveTheme = ResolveEffectiveTheme(requestedTheme);
        var changed = _currentTheme != requestedTheme || _effectiveTheme != effectiveTheme;

        _currentTheme = requestedTheme;
        _effectiveTheme = effectiveTheme;

        _resourceApplier.ApplyTheme(effectiveTheme);

        if (raiseEvent && changed)
        {
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(requestedTheme, effectiveTheme));
        }
    }

    private AppTheme ResolveEffectiveTheme(AppTheme requestedTheme)
    {
        if (requestedTheme != AppTheme.System)
        {
            return requestedTheme;
        }

        var systemTheme = _systemThemeProvider.GetSystemTheme();

        return systemTheme == AppTheme.Dark ? AppTheme.Dark : AppTheme.Light;
    }
}
