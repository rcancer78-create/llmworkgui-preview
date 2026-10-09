namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// Append-only store of health transitions. There is deliberately no update or delete operation:
/// the recovery audit must not be rewritable, and bulk trimming by age belongs to the retention
/// service, not to callers (ТЗ §7.2, §14).
/// </summary>
public interface IHealthEventRepository
{
    Task AppendAsync(HealthEventRecord healthEvent, CancellationToken cancellationToken = default);

    /// <summary>Finds the exact durable observation used to reconcile a lost completion acknowledgement.</summary>
    async Task<HealthEventRecord?> FindByIdAsync(string scopeType, string scopeId, string id,
        CancellationToken cancellationToken = default) =>
        (await ListByScopeAsync(scopeType, scopeId, cancellationToken: cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(item => item.Id == id);

    /// <summary>
    /// Returns the transitions of one scope, newest first, optionally capped by <paramref name="limit"/>.
    /// Transitions sharing a timestamp are returned in reverse insertion order: several transitions can
    /// legitimately occur at the same instant, and the audit must still read in the order they happened.
    /// </summary>
    Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(
        string scopeType,
        string scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent transitions across all scopes, newest first.</summary>
    Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
