namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed class MirasimOptions
{
    public const string SectionName = "Mirasim";

    public const string DefaultHostname = "127.0.0.1";

    public const int DefaultPort = 4970;

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultTurnHardTimeout = TimeSpan.FromMinutes(10);

    public string Hostname { get; set; } = DefaultHostname;

    public int Port { get; set; } = DefaultPort;

    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;
    /// <summary>Deadline for model execution and its stream, separate from short control requests.</summary>
    public TimeSpan TurnHardTimeout { get; set; } = DefaultTurnHardTimeout;
    public int MaxRetainedSessions { get; set; } = 128;
    public int MaxRetainedExecutions { get; set; } = 256;

    public static bool IsLoopbackHostname(string? hostname)
    {
        return string.Equals(hostname, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMilliseconds(4294967294L))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), RequestTimeout,
                "The Mirasim request timeout must be positive and within the timer range (4294967294 milliseconds).");
        if (TurnHardTimeout <= TimeSpan.Zero || TurnHardTimeout > TimeSpan.FromMilliseconds(4294967294L))
            throw new ArgumentOutOfRangeException(nameof(TurnHardTimeout), TurnHardTimeout,
                "The Mirasim turn deadline must be positive and within the timer range (4294967294 milliseconds).");
        if (MaxRetainedSessions is < 1 or > 4096 || MaxRetainedExecutions is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedSessions), "Mirasim retained ownership limits are invalid.");
        if (!IsLoopbackHostname(Hostname))
        {
            throw new ArgumentException(
                $"Hostname '{Hostname}' is not allowed. Only loopback hostnames '127.0.0.1' or 'localhost' are permitted.",
                nameof(Hostname));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Port),
                Port,
                "Port must be between 1 and 65535.");
        }
    }
}
