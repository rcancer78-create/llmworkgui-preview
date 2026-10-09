using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// Repository for persisting and retrieving provider profile configuration.
/// </summary>
public interface IProviderProfileRepository
{
    Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Updates the profile. An omitted/null separate secret reference preserves the existing reference.</summary>
    Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default);
    /// <summary>Returns the revision committed in the same write; deleted/recreated IDs keep increasing revisions.</summary>
    async Task<long> UpsertReturningRevisionAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
    {
        await UpsertAsync(profile, apiKeySecretReference, cancellationToken).ConfigureAwait(false);
        return (profile.Revision ?? -1) + 1;
    }
    Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
