namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Thrown by <see cref="CursorAcpHandshakeValidator"/> when the ACP initialize result violates the
/// ADR-0003 contract. The client converts it into a degraded readiness result.
/// </summary>
public sealed class CursorAcpProtocolViolationException : Exception
{
    public CursorAcpProtocolViolationException(CursorAcpProtocolViolationKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public CursorAcpProtocolViolationKind Kind { get; }
}
