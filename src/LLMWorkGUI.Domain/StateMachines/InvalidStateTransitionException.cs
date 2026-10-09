namespace LLMWorkGUI.Domain.StateMachines;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(string machineName, object from, object to, string reason)
        : base($"{machineName} cannot transition from '{from}' to '{to}': {reason}.")
    {
        MachineName = machineName;
        From = from;
        To = to;
        Reason = reason;
    }

    public string MachineName { get; }

    public object From { get; }

    public object To { get; }

    public string Reason { get; }
}
