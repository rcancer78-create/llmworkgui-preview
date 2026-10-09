namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public sealed class OpenCodeServerStartupException : Exception
{
    public OpenCodeServerStartupException(string message)
        : base(message)
    {
    }

    public OpenCodeServerStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
