namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Thrown when a model selection violates the parameterized override contract (ADR-0003 §7,
/// AC2/AC3). Formatting to the wire and validation both fail closed instead of silently dropping
/// unsupported parameters or values.
/// </summary>
public sealed class CursorAcpModelSelectionException : Exception
{
    public CursorAcpModelSelectionException(CursorAcpSelectionFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Stable rejection reason for tests and UI mapping.</summary>
    public CursorAcpSelectionFailureKind Kind { get; }
}
