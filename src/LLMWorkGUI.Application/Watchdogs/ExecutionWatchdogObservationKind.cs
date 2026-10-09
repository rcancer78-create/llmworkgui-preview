namespace LLMWorkGUI.Application.Watchdogs;

/// <summary>
/// Kinds of observations a turn-level watchdog publishes. Process liveness, transport
/// activity and model silence are tracked separately; model silence is an observation,
/// never an automatic failure.
/// </summary>
public enum ExecutionWatchdogObservationKind
{
    /// <summary>The supervised process execution is still pending (the process is alive).</summary>
    ProcessLiveness,

    /// <summary>Output/transport activity was observed for the turn.</summary>
    TransportActivity,

    /// <summary>No transport output was observed for the configured interval.</summary>
    ModelSilence,

    /// <summary>Session/protocol evidence was observed for the turn.</summary>
    SessionConfirmed,

    /// <summary>The session-confirmation deadline elapsed without evidence.</summary>
    SessionConfirmationTimeoutElapsed,

    /// <summary>The turn hard timeout elapsed.</summary>
    TurnHardTimeoutElapsed,

    /// <summary>The caller cancelled the turn.</summary>
    UserCancellationRequested,

    /// <summary>The supervised process exited.</summary>
    ProcessExited
}
