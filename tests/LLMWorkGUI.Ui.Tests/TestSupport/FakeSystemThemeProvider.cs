using LLMWorkGUI.App.Services;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeSystemThemeProvider : ISystemThemeProvider
{
    public AppTheme SystemTheme { get; set; } = AppTheme.Light;

    public AppTheme GetSystemTheme()
    {
        return SystemTheme;
    }
}
