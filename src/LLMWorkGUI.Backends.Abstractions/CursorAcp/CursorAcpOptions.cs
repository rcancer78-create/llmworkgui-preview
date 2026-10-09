namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Timing and framing options of the native Cursor ACP backend. Defaults are safe for a local
/// stdio transport; every timeout is bounded so a stuck child process cannot block the GUI.
/// </summary>
public sealed class CursorAcpOptions
{
    public const string DefaultClientName = "LLMWorkGUI";

    public const string DefaultClientVersion = "1.0.0";

    /// <summary>Client name reported to the agent in the ACP initialize request.</summary>
    public string ClientName { get; set; } = DefaultClientName;

    /// <summary>Client version reported to the agent in the ACP initialize request.</summary>
    public string ClientVersion { get; set; } = DefaultClientVersion;

    /// <summary>Bound on the 'cursor-agent --version' discovery probe.</summary>
    public TimeSpan VersionProbeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Bound on the ACP initialize request/response exchange.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default bound applied to every JSON-RPC request when no explicit timeout is passed.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound on a single prompt turn from dispatch until terminal evidence. The hard timeout is
    /// applied to the turn only, never to the long-lived ACP process (ТЗ §6.8).
    /// </summary>
    public TimeSpan TurnHardTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Bound on awaiting terminal cancellation evidence after an in-band <c>session/cancel</c>.
    /// Expiry without confirmation yields <c>Ambiguous</c> and the writer lock is retained
    /// (ADR-0003 §6.2, §10.4).
    /// </summary>
    public TimeSpan CancellationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bound on the graceful stop of the managed 'cursor-agent acp' process tree.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Capacity of the bounded outbound frame queue that provides JSON-RPC backpressure.</summary>
    public int OutboundQueueCapacity { get; set; } = 64;

    /// <summary>Capacity of the bounded inbound notification queue.</summary>
    public int NotificationQueueCapacity { get; set; } = 256;

    /// <summary>Maximum decoded UTF-16 characters in one inbound JSON-RPC line, excluding its newline.</summary>
    public int MaximumInboundFrameCharacters { get; set; } = 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientName))
        {
            throw new ArgumentException("Cursor ACP client name must not be empty.", nameof(ClientName));
        }

        if (string.IsNullOrWhiteSpace(ClientVersion))
        {
            throw new ArgumentException("Cursor ACP client version must not be empty.", nameof(ClientVersion));
        }

        EnsurePositive(VersionProbeTimeout, nameof(VersionProbeTimeout));
        EnsurePositive(HandshakeTimeout, nameof(HandshakeTimeout));
        EnsurePositive(RequestTimeout, nameof(RequestTimeout));
        EnsurePositive(TurnHardTimeout, nameof(TurnHardTimeout));
        EnsurePositive(CancellationTimeout, nameof(CancellationTimeout));
        EnsurePositive(ShutdownTimeout, nameof(ShutdownTimeout));

        if (OutboundQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutboundQueueCapacity),
                OutboundQueueCapacity,
                "The outbound JSON-RPC queue capacity must be positive.");
        }

        if (NotificationQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NotificationQueueCapacity),
                NotificationQueueCapacity,
                "The inbound JSON-RPC notification queue capacity must be positive.");
        }

        if (MaximumInboundFrameCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInboundFrameCharacters),
                MaximumInboundFrameCharacters, "The inbound JSON-RPC frame limit must be positive.");
        }
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "The Cursor ACP timeout must be positive.");
        }
    }
}
