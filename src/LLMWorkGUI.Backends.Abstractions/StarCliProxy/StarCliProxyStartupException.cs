namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Raised when a managed star-cliproxy instance cannot be started. The message always carries a
/// precise blocker; no silent fallback through OpenCode or a direct CLI exists (ADR-0007).
/// </summary>
public sealed class StarCliProxyStartupException : Exception
{
    public StarCliProxyStartupException(string message)
        : base(message)
    {
    }

    public StarCliProxyStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
