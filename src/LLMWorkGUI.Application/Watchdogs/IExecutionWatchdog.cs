namespace LLMWorkGUI.Application.Watchdogs;

public interface IExecutionWatchdog
{
    /// <summary>
    /// Runs and observes one turn until a terminal classification. The process is launched
    /// through the typed process launch contract; a hard timeout of a long-lived process ends
    /// the turn without killing the backend process.
    /// </summary>
    Task<ExecutionTurnResult> WatchAsync(
        ExecutionWatchdogRequest request,
        IProgress<ExecutionWatchdogObservation>? observationProgress = null,
        CancellationToken turnCancellationToken = default);
}
