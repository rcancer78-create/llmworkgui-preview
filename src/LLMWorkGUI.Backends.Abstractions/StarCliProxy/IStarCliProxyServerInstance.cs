namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// A running loopback star-cliproxy instance owned by the application. One managed instance is
/// used per immutable account context until a live spike proves safe multi-context reuse
/// (ADR-0007 §6, ТЗ §6.11a).
/// </summary>
public interface IStarCliProxyServerInstance
{
    string InstanceId { get; }

    int? ProcessId { get; }

    int AssignedPort { get; }

    Uri BaseUrl { get; }

    /// <summary>Absolute CODEX_HOME passed to the owned process environment, or null for other contexts.</summary>
    string? CodexHomePath { get; }

    string ConfigFilePath { get; }

    /// <summary>In-memory proxy API key for this instance. Never logged or placed on a command line.</summary>
    string ApiKey { get; }

    bool IsAlive { get; }

    DateTimeOffset StartedAtUtc { get; }
}
