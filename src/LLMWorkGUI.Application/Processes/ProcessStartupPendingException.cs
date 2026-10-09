namespace LLMWorkGUI.Application.Processes;

/// <summary>A protocol operation cannot complete because startup, existing ownership or cleanup is pending.
/// The supervisor retains the process handles until cleanup completes.</summary>
public sealed class ProcessStartupPendingException(Task cleanupCompletion,
    ProcessTerminationReason reason = ProcessTerminationReason.StartupPending)
    : Exception(reason == ProcessTerminationReason.StartupPending
        ? "Process startup is pending; ownership is retained. Do not retry this execution."
        : "Process ownership/cleanup is pending after startup; termination is unconfirmed. Do not retry this execution.")
{
    public Task CleanupCompletion { get; } = cleanupCompletion ?? throw new ArgumentNullException(nameof(cleanupCompletion));
    public ProcessTerminationReason Reason { get; } = reason;
}
