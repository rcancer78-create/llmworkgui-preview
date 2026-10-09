namespace LLMWorkGUI.Application.Processes;

/// <summary>
/// Handle of a long-lived supervised process whose standard input and standard output are owned by
/// an explicit protocol transport (for example ACP JSON-RPC over stdio, ТЗ §4.2, §6.8).
/// </summary>
/// <remarks>
/// <para>
/// The regular <see cref="IProcessSupervisor.ExecuteAsync"/> contract is run-to-completion: the
/// supervisor reads <c>stdout</c> itself into the run-directory spool and never hands the stream to
/// a caller. A duplex protocol process needs the opposite ownership, so it is launched through
/// <see cref="IProcessSupervisor.StartProtocolProcessAsync"/> instead.
/// </para>
/// <para>
/// Ownership contract:
/// <list type="bullet">
/// <item><description>the caller owns <see cref="StandardInput"/> and <see cref="StandardOutput"/>
/// and must not close them directly — disposing the session closes them;</description></item>
/// <item><description>the supervisor still owns liveness, the bounded <c>stderr</c> spool in
/// <see cref="RunDirectory"/> and process-tree termination;</description></item>
/// <item><description><c>stdout</c> is never spooled, because the protocol transport consumes it
/// and the frames may carry workspace content.</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IProtocolProcessSession : IAsyncDisposable
{
    /// <summary>Execution this process belongs to.</summary>
    string ExecutionId { get; }

    /// <summary>OS process id of the launched child.</summary>
    int ProcessId { get; }

    /// <summary>Unique supervised launch generation within this application process; zero means unknown.</summary>
    long ProcessGeneration => 0;

    /// <summary>Run/spool directory owned by the supervisor, outside any project checkout.</summary>
    string RunDirectory { get; }

    /// <summary>Path of the bounded standard-error spool file.</summary>
    string StandardErrorLogPath { get; }

    /// <summary>Moment the process was started.</summary>
    DateTimeOffset StartedAtUtc { get; }

    /// <summary>False once the process exited or the session was stopped.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Writable stream bound to the child standard input. Owned by the protocol transport; the
    /// supervisor never writes to it and never closes it while the session is running.
    /// </summary>
    Stream StandardInput { get; }

    /// <summary>
    /// Readable stream bound to the child standard output. Owned by the protocol transport; the
    /// supervisor does not read or spool it.
    /// </summary>
    Stream StandardOutput { get; }

    /// <summary>
    /// Completes when the process exits, carrying the terminal supervision evidence. It never
    /// faults for a normal non-zero exit.
    /// </summary>
    Task<ProcessExecutionResult> Completion { get; }

    /// <summary>
    /// Requests a graceful stop and terminates the remaining process tree after the bounded
    /// shutdown window. Idempotent. Caller cancellation or a cleanup deadline does not discard ownership;
    /// implementations may report ProcessStartupPendingException with CleanupPending and a retained cleanup task.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
