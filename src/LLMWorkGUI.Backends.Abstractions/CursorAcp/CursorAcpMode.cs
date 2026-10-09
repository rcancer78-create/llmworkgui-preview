namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// ACP execution modes of the Cursor backend (ADR-0003 §5). <see cref="Unknown"/> is the
/// fail-closed value for any identifier outside <c>ask</c>, <c>plan</c> and <c>agent</c>.
/// </summary>
public enum CursorAcpMode
{
    /// <summary>Read-only Q&amp;A mode (<c>ask</c>).</summary>
    Ask,

    /// <summary>Read-only planning mode (<c>plan</c>).</summary>
    Plan,

    /// <summary>Default write mode executing workspace changes (<c>agent</c>).</summary>
    Agent,

    /// <summary>Unrecognized mode identifier; never selectable or sendable.</summary>
    Unknown
}
