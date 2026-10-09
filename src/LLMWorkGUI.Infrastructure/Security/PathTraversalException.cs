namespace LLMWorkGUI.Infrastructure.Security;

public sealed class PathTraversalException : Exception
{
    public PathTraversalException(string message)
        : base(message)
    {
    }

    public PathTraversalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
