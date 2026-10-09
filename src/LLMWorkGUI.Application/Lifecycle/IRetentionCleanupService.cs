namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>
/// Long-term retention cleanup of the hardening phase. The service deletes only stale diagnostic
/// archives and abandoned scratch workspaces and archives old audit/event records before removing
/// them; active runs, recent bundles and immutable workflow versions are never touched (ROADMAP Phase
/// 12).
/// </summary>
public interface IRetentionCleanupService
{
    /// <summary>The policy applied by this service.</summary>
    RetentionPolicy Policy { get; }

    Task<RetentionCleanupReport> CleanupAsync(CancellationToken cancellationToken = default);
}
