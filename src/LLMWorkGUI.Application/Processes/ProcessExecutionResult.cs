namespace LLMWorkGUI.Application.Processes;

public sealed record ProcessExecutionResult
{
    public required string ExecutionId { get; init; }

    public int? ProcessId { get; init; }

    public required ProcessTerminationReason TerminationReason { get; init; }

    public int? ExitCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Result observation time. For StartupPending or CleanupPending this is not proof of physical process exit.</summary>
    public required DateTimeOffset ExitedAtUtc { get; init; }

    public required string RunDirectory { get; init; }

    public required string StandardOutputLogPath { get; init; }

    public required string StandardErrorLogPath { get; init; }

    public required long StandardOutputBytes { get; init; }

    public required long StandardErrorBytes { get; init; }

    public required string StandardOutputHead { get; init; }

    public required string StandardOutputTail { get; init; }

    public required string StandardErrorHead { get; init; }

    public required string StandardErrorTail { get; init; }

    /// <summary>
    /// Output capture is incomplete: memory retention exceeded its bound, or an owned output
    /// spool/read operation failed. A spool failure does not imply that a byte limit was exceeded.
    /// Regular supervision continues pipe draining and refuses an ordinary successful result.
    /// </summary>
    public required bool OutputOverflowed { get; init; }

    /// <summary>An owned spool/read operation failed; unrelated to how much output the model produced.</summary>
    public bool OutputCaptureIncomplete { get; init; }

    /// <summary>The actual retained-byte limit was exceeded, independently of any spool/read failure.</summary>
    public bool OutputLimitExceeded { get; init; }

    public string? FailureMessage { get; init; }

    public TimeSpan Duration => ExitedAtUtc - StartedAtUtc;
}
