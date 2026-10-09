namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Thrown when the discovered Cursor ACP model catalog snapshot cannot be deserialized or
/// violates the discovery schema. A malformed catalog is never silently truncated or repaired.
/// </summary>
public sealed class CursorAcpCatalogFormatException : Exception
{
    public CursorAcpCatalogFormatException(string message)
        : base(message)
    {
    }

    public CursorAcpCatalogFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
