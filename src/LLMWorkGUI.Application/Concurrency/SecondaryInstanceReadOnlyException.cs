namespace LLMWorkGUI.Application.Concurrency;

public sealed class SecondaryInstanceReadOnlyException : InvalidOperationException
{
    public SecondaryInstanceReadOnlyException(string message)
        : base(message)
    {
    }
}
