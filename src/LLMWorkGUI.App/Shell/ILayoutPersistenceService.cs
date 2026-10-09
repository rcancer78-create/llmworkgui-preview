namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Persists and restores the unified shell layout. Implementations must never throw on a missing,
/// corrupt or unwritable store: the shell always has to be able to fall back to defaults.
/// </summary>
public interface ILayoutPersistenceService
{
    /// <summary>Loads the stored layout, or defaults when nothing valid is stored.</summary>
    ShellLayoutState Load();

    /// <summary>Stores the supplied layout, replacing any previous state.</summary>
    void Save(ShellLayoutState state);
}
