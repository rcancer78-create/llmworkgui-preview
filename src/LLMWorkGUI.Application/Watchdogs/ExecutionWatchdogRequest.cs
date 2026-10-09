using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Application.Watchdogs;

public sealed class ExecutionWatchdogRequest
{
    public required ProcessStartSpecification Specification { get; init; }

    /// <summary>
    /// Deadline for observing session/protocol evidence (first transport activity). When no
    /// evidence is observed within the deadline, an alive process is classified as an
    /// orchestration failure instead of being treated as a model failure. When <c>null</c>,
    /// the deadline is disabled.
    /// </summary>
    public TimeSpan? SessionConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Hard deadline for the whole turn. It never kills a long-lived server process.</summary>
    public TimeSpan TurnHardTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Interval after which continued absence of transport output is reported as
    /// <see cref="ExecutionWatchdogObservationKind.ModelSilence"/>. Silence is an
    /// observation, not a failure.
    /// </summary>
    public TimeSpan SilenceObservationInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when the supervised process is a long-lived backend (server/ACP). A turn hard
    /// timeout or cancellation then ends the turn without terminating the process.
    /// </summary>
    public bool ProcessIsLongLived { get; init; }

    /// <summary>
    /// Lifetime of a long-lived backend process, owned by the caller. Cancelling it ends the
    /// process itself, independent of turn timeouts.
    /// </summary>
    public CancellationToken ProcessLifetimeToken { get; init; } = CancellationToken.None;
}
