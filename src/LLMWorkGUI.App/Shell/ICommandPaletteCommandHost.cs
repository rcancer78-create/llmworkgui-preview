using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Operations the default command palette set can invoke. The shell implements it, so the palette stays
/// free of screen, theme and workflow wiring and can be unit tested with a stub host.
/// </summary>
public interface ICommandPaletteCommandHost
{
    /// <summary>The normative screen catalog in navigation order.</summary>
    IReadOnlyList<ScreenViewModel> Screens { get; }

    void NavigateTo(ScreenId screenId);

    void SetTheme(AppTheme theme);

    void RefreshCli();

    void OpenWorkflowStudio();

    void OpenWorkflowActivityMonitor();

    void ImportWorkflow();

    void ExportWorkflow();
}
