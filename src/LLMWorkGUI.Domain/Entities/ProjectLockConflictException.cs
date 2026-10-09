namespace LLMWorkGUI.Domain.Entities;

public sealed class ProjectLockConflictException : InvalidOperationException
{
    public ProjectLockConflictException(string message)
        : base(message)
    {
    }
}
