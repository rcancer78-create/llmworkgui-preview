namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Owns the lifecycle of managed <c>cursor-agent acp</c> processes through the shared process
/// supervisor (ADR-0003 §11, ТЗ §4.2): launch without shell interpolation, a dedicated spool and
/// working directory outside any checkout, hidden window, and process-tree termination on stop.
/// </summary>
public interface ICursorAcpProcessManager
{
    /// <summary>
    /// Starts one managed ACP process. Returns a degraded result (never throws) when the executable
    /// is missing/unresolved or the supervisor fails the launch.
    /// </summary>
    Task<CursorAcpProcessStartResult> StartAsync(
        CursorAcpProcessStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a session created by this manager: cancels the owned lifetime so the process supervisor
    /// terminates the whole process tree, then waits for the bounded shutdown window.
    /// </summary>
    Task StopAsync(
        ICursorAcpProcessSession session,
        CancellationToken cancellationToken = default);
}
