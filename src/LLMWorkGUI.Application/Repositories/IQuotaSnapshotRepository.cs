using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IQuotaSnapshotRepository
{
    Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<QuotaSnapshot?> GetLatestForAccountAsync(
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    async Task<QuotaSnapshot?> GetLatestAccountWideAsync(string accountId, CancellationToken cancellationToken = default) =>
        (await ListLatestByAccountIdAsync(accountId, cancellationToken).ConfigureAwait(false))
            .Where(snapshot => snapshot.AccountId == accountId && snapshot.ModelId is null)
            .OrderByDescending(snapshot => snapshot.CapturedAt).FirstOrDefault();

    Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
