namespace LLMWorkGUI.Infrastructure.Logging;

public sealed class RedactedException : Exception
{
    public RedactedException(string originalExceptionType, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalExceptionType);

        OriginalExceptionType = originalExceptionType;
    }

    public string OriginalExceptionType { get; }
}
