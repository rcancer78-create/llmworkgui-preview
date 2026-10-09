namespace LLMWorkGUI.Application.Processes;

public sealed class ProcessSupervisorOptions
{
    public const string SectionName = "ProcessSupervisor";

    public const string StandardOutputFileName = "stdout.log";

    public const string StandardErrorFileName = "stderr.log";

    public const long DefaultOutputMemoryLimitBytes = 10 * 1024 * 1024;

    public const int DefaultOutputHeadRetentionBytes = 256 * 1024;

    public const int DefaultOutputTailRetentionBytes = 256 * 1024;

    public long OutputMemoryLimitBytes { get; set; } = DefaultOutputMemoryLimitBytes;

    /// <summary>
    /// Maximum UTF-8 bytes retained on disk for a long-lived protocol process's stderr.
    /// After this prefix is full, stderr continues draining so the protocol cannot deadlock.
    /// Standard output remains exclusively owned by the protocol transport.
    /// </summary>
    public long ProtocolStandardErrorSpoolLimitBytes { get; set; } = 10 * 1024 * 1024;

    public int OutputHeadRetentionBytes { get; set; } = DefaultOutputHeadRetentionBytes;

    public int OutputTailRetentionBytes { get; set; } = DefaultOutputTailRetentionBytes;

    public int OutputChannelCapacity { get; set; } = 256;

    public int StreamReadBufferSize { get; set; } = 4096;

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bounded grace window granted to an in-flight <c>Process.Start()</c> when the caller cancels
    /// concurrently. Losing that race is not evidence of a startup failure, so a process that does
    /// start inside this window is reported as started and then terminated through the normal
    /// supervised path with a deterministic result.
    /// </summary>
    public TimeSpan StartupGraceWindow { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan? TurnTimeout { get; set; }

    public TimeSpan? InactivityTimeout { get; set; }

    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
