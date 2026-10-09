namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Handle of one managed <c>cursor-agent acp</c> process owned by the process supervisor. The
/// supervisor owns liveness, the bounded standard-error spool and process-tree termination; the
/// session owns the JSON-RPC transport bound to the live stdio duplex channel
/// (<c>ProcessStdinPolicy.DirectProtocolTransport</c>) and closes it on disposal.
/// </summary>
public interface ICursorAcpProcessSession : IAsyncDisposable
{
    string ExecutionId { get; }

    /// <summary>Best-effort OS process id of the managed child; null when it could not be resolved.</summary>
    int? ProcessId { get; }

    /// <summary>Generation issued by the owner of the managed process; zero means unknown.</summary>
    long ProcessGeneration => 0;

    /// <summary>Dedicated working directory of the managed process, outside any project checkout.</summary>
    string WorkingDirectory { get; }

    /// <summary>Run/spool directory owned by the process supervisor for this execution.</summary>
    string RunDirectory { get; }

    DateTimeOffset StartedAtUtc { get; }

    bool IsRunning { get; }

    /// <summary>
    /// JSON-RPC transport bound to the standard input/output of the live managed process, or null
    /// when the supervisor could not provide a duplex channel. A null transport is a degraded state
    /// and never a silent CLI print-mode fallback (ADR-0003 §1, §7.2).
    /// </summary>
    IJsonRpcTransport? Transport { get; }
}
