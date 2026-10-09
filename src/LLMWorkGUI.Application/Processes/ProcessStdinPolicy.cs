namespace LLMWorkGUI.Application.Processes;

/// <summary>
/// Declares how standard input is attached to a supervised process.
/// Non-interactive launches must use <see cref="Closed"/> so a child process can never
/// block forever waiting for a required parameter on stdin.
/// </summary>
public enum ProcessStdinPolicy
{
    /// <summary>Standard input is redirected and immediately closed; every read returns EOF.</summary>
    Closed,

    /// <summary>
    /// Standard input is redirected and immediately closed, matching the null-device
    /// semantics of a non-interactive launch.
    /// </summary>
    Null,

    /// <summary>
    /// Standard input is reserved for an explicit protocol transport (for example ACP over
    /// stdio). It is redirected but not closed, because the owning transport writes to it.
    /// </summary>
    DirectProtocolTransport
}
