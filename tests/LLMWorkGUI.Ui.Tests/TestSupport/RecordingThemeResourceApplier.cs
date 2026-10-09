using LLMWorkGUI.App.Services;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class RecordingThemeResourceApplier : IThemeResourceApplier
{
    public List<AppTheme> AppliedThemes { get; } = new();

    public void ApplyTheme(AppTheme effectiveTheme)
    {
        AppliedThemes.Add(effectiveTheme);
    }
}
