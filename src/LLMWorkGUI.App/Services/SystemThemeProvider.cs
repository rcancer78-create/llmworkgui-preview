using System.IO;
using Microsoft.Win32;

namespace LLMWorkGUI.App.Services;

public sealed class SystemThemeProvider : ISystemThemeProvider
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    public AppTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);

            if (key?.GetValue(AppsUseLightThemeValueName) is int value)
            {
                return value == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException
                or ObjectDisposedException)
        {
        }

        return AppTheme.Light;
    }
}
