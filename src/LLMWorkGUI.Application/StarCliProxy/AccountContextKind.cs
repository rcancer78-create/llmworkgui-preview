namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>Account context kind behind the star-cliproxy boundary (ADR-0007).</summary>
public enum AccountContextKind
{
    /// <summary>Codex account bound to its own immutable absolute CODEX_HOME directory.</summary>
    Codex,

    /// <summary>AGY account bound to a saved profile selected only through agy-profile.</summary>
    Agy
}
