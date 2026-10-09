namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Serializable layout of the unified shell: panel widths, collapse state, the active screen and the
/// selected theme (ROADMAP Phase 11). It carries no behavior so it can be round-tripped through JSON.
/// </summary>
public sealed class ShellLayoutState
{
    public const double DefaultLeftPaneWidth = 230;
    public const double DefaultRightPaneWidth = 300;

    public double LeftPaneWidth { get; set; } = DefaultLeftPaneWidth;

    public double RightPaneWidth { get; set; } = DefaultRightPaneWidth;

    public bool IsLeftPaneVisible { get; set; } = true;

    public bool IsRightPaneVisible { get; set; } = true;

    public string ActiveScreen { get; set; } = nameof(ViewModels.ScreenId.Workspace);

    public string Theme { get; set; } = string.Empty;
}
