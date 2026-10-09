namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public interface IOpenCodeEventStreamService
{
    /// <summary>Reports buffer eviction, spool pressure or write failure for the latest started subscription.</summary>
    bool IsOverflowed { get; }

    /// <summary>
    /// Path owned by the latest started subscription. Each subscription has a separate file retained after disposal;
    /// the caller manages retention of closed files when enabling spooling.
    /// </summary>
    string? SpoolFilePath { get; }

    /// <summary>Returns buffered events for the latest started subscription, including after it ends.</summary>
    IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null);

    /// <summary>
    /// Starts an independent subscription when enumerated. Diagnostic properties refer to the latest started
    /// subscription; reconnect preserves its buffer and spool. Spooling is disabled unless a directory is configured.
    /// </summary>
    IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes to the native instance belonging to the session's project directory.</summary>
    IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
        string? sessionId,
        CancellationToken cancellationToken,
        string? directory) => SubscribeAsync(sessionId, cancellationToken);
}
