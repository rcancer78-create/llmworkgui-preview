namespace LLMWorkGUI.App.Services;

public sealed class ThemeChangedEventArgs : EventArgs
{
    public ThemeChangedEventArgs(AppTheme requestedTheme, AppTheme effectiveTheme)
    {
        RequestedTheme = requestedTheme;
        EffectiveTheme = effectiveTheme;
    }

    public AppTheme RequestedTheme { get; }

    public AppTheme EffectiveTheme { get; }
}
