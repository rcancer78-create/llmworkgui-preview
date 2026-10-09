namespace LLMWorkGUI.Application.Processes;

public interface IProcessSupervisor
{
    /// <summary>Waits for retained process cleanup for this execution. Call only after StartupPending or CleanupPending.
    /// Completion proves cleanup; cancellation of the wait does not cancel cleanup.</summary>
    Task WaitForStartupCleanupAsync(string executionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This supervisor cannot prove late-start cleanup completion.");

    /// <summary>
    /// Runs a process to completion. The supervisor owns both output streams, spools them into the
    /// run directory and closes standard input according to
    /// <see cref="ProcessStartSpecification.StdinPolicy"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// DirectProtocolTransport requires StartProtocolProcessAsync, which hands the duplex streams
    /// to their explicit transport owner; regular execution cannot expose that channel.
    /// </exception>
    Task<ProcessExecutionResult> ExecuteAsync(
        ProcessStartSpecification specification,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a long-lived process and hands its standard input and standard output to the caller so
    /// an explicit protocol transport can own the duplex channel (ТЗ §4.2, §6.8, ADR-0003 §1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specification must declare <see cref="ProcessStdinPolicy.DirectProtocolTransport"/>: any
    /// other policy is a contract violation, because the supervisor would close the very stream the
    /// transport needs. Standard error is still spooled with a bounded writer, liveness and
    /// process-tree termination remain owned by the supervisor, and standard output is deliberately
    /// not spooled because the protocol frames may carry workspace content.
    /// </para>
    /// <para>
    /// This is an optional capability. Supervisors that do not own real OS processes (in-memory test
    /// doubles, scripted fakes) keep the default implementation, which fails loudly with
    /// <see cref="NotSupportedException"/> instead of pretending to provide a duplex channel.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The supervisor cannot provide a real duplex stdio channel.
    /// </exception>
    /// <exception cref="ProcessStartupPendingException">
    /// Startup, an existing execution, or cleanup still owns this execution ID. Reason distinguishes
    /// StartupPending from CleanupPending. No new session is handed over; do not retry until cleanup.
    /// </exception>
    Task<IProtocolProcessSession> StartProtocolProcessAsync(
        ProcessStartSpecification specification,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"The process supervisor '{GetType().Name}' does not support launching a duplex protocol " +
            "process. Only a supervisor that owns real OS processes can hand over the stdio channel.");
}
