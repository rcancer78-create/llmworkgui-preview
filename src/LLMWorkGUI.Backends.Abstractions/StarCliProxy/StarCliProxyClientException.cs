namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Raised when a star-cliproxy HTTP interaction fails in a way the caller must treat as a
/// routing/transport failure rather than a successful turn.
/// </summary>
public sealed class StarCliProxyClientException : Exception
{
    public StarCliProxyClientException(string message)
        : base(message)
    {
    }

    public StarCliProxyClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
