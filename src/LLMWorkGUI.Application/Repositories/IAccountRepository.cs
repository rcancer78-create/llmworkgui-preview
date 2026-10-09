using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Repositories;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(string providerProfileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Saves the complete account, including an explicit null to detach its secret reference.</summary>
    Task SaveAsync(Account account, CancellationToken cancellationToken = default);
    /// <summary>Conditionally replaces the key and invalidates prior auth evidence, preserving other settings.</summary>
    Task ReplaceSecretReferenceAsync(string accountId, string profileId, string? expectedReference,
        string newReference, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Conditional account credential binding is unavailable.");
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateAuthStateAsync(string id, AuthState authState, CancellationToken cancellationToken = default);
    Task UpdateCooldownAsync(string id, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken = default);
    Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default);
}
