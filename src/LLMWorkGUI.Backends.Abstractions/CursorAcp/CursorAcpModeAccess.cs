namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Declared workspace access of an ACP mode (ADR-0003 §5).</summary>
public enum CursorAcpModeAccess
{
    /// <summary>The mode declares no workspace writes (<c>read-only</c>).</summary>
    ReadOnly,

    /// <summary>The mode may modify the workspace (<c>write</c>).</summary>
    Write,

    /// <summary>Unrecognized access declaration; never treated as read-only.</summary>
    Unknown
}
