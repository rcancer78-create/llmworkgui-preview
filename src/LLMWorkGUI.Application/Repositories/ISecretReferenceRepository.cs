using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// Reference-only secret metadata (ADR-0005 §2.1). Every member takes or returns a URN and its
/// lifecycle state; no member can read or write a secret value.
/// </summary>
public interface ISecretReferenceRepository
{
    Task<SecretReferenceMetadata?> GetAsync(string reference, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecretReferenceMetadata>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// All owners bound to one reference. A URN may be shared by a provider profile and an account,
    /// so this is never assumed to hold at most one row.
    /// </summary>
    Task<IReadOnlyList<SecretReferenceOwnerBinding>> ListOwnersAsync(
        string reference,
        CancellationToken cancellationToken = default);

    Task InsertAsync(SecretReferenceMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Records that the value behind an existing reference was overwritten in place.</summary>
    Task MarkRotatedAsync(string reference, DateTimeOffset lastRotatedAtUtc, CancellationToken cancellationToken = default);

    Task UpdateStateAsync(
        string reference,
        SecretReferenceState state,
        DateTimeOffset? revokedAtUtc,
        CancellationToken cancellationToken = default);

    Task AddOwnerAsync(SecretReferenceOwnerBinding binding, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
