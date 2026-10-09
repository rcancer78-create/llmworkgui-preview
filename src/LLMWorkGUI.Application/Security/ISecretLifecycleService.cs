using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

/// <summary>
/// The secret lifecycle of ADR-0005 §5: create, resolve status, rotate in place and revoke. It is the
/// only component that turns a secret value into a durable URN, and the only component that is
/// allowed to resolve a URN back to a value for a provider send.
///
/// Callers must consult <see cref="GetStatusAsync"/> before a value is needed: a reference that is
/// <see cref="SecretReferenceState.Missing"/> or <see cref="SecretReferenceState.Revoked"/> must fail
/// closed rather than degrade into an unauthenticated request.
/// </summary>
public interface ISecretLifecycleService
{
    /// <summary>Deletes the selected revision and durably revokes unshared credentials before payload cleanup.</summary>
    Task<ProviderDeletionResult> DeleteProviderConfigurationAsync(string providerId, long expectedRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Coordinated provider deletion is unavailable.");

    /// <summary>Retries committed payload cleanup; returns the number still pending.</summary>
    Task<int> RetryPendingSecretCleanupAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <summary>Saves header metadata and optional API key with the provider; compensates newly created header secrets on failure.</summary>
    Task<Providers.ProviderConfigurationSaveResult> SaveProviderConfigurationAsync(ProviderProfile profile,
        IReadOnlyList<Providers.CustomProviderHeader> headers, string? rawApiKey = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This secret lifecycle cannot persist provider headers.");

    /// <summary>
    /// The effective state of a URN. Never throws for a malformed legacy reference: such a reference
    /// is reported as <see cref="SecretReferenceState.Missing"/> so it fails closed.
    /// </summary>
    Task<SecretReferenceStatus> GetStatusAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new value under a new URN and registers its metadata. When the store cannot
    /// overwrite a payload in place the reference is still created here, so the value is never
    /// written without a reference that can describe it.
    /// </summary>
    Task<SecretReferenceStatus> CreateAsync(
        string rawSecret,
        SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey,
        SecretReferenceOwnerBinding? owner = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites the value behind an existing URN, keeping the URN itself (ADR-0005 §5.1). A
    /// <see cref="SecretReferenceState.Revoked"/> reference is never reused: a new URN is registered
    /// instead and the revoked one stays unusable. A store that cannot overwrite a payload in place
    /// cannot keep the URN; the value then moves to a new reference and the superseded one is revoked
    /// before this call returns.
    /// </summary>
    Task<SecretReferenceStatus> RotateAsync(string reference, string rawSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the payload and moves the URN to <see cref="SecretReferenceState.Revoked"/>. The state
    /// is recorded before the payload is removed, so a surviving file can never make a revoked
    /// reference usable again (ADR-0005 §5.2).
    /// </summary>
    Task<SecretReferenceStatus> RevokeAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>Records that a profile may use this URN. Several owners may share one URN.</summary>
    Task<SecretReferenceStatus> BindAsync(
        string reference,
        SecretReferenceOwnerBinding owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a provider API key behind a fresh URN and persists the profile with that URN.
    /// A superseded reference is only revoked after the profile points at its replacement, and a
    /// failure to persist compensates the new reference while leaving the previous key working.
    /// </summary>
    Task<SecretReferenceStatus> SaveProviderApiKeyAsync(
        ProviderProfile profile,
        string rawSecret,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes the profile's API key reference. A profile without a reference is a no-op.</summary>
    Task RevokeProviderApiKeyAsync(string providerProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an account credential behind a fresh URN. The account is read inside the write lock, so
    /// overlapping saves cannot leave a replacement active that the account no longer points at.
    /// </summary>
    Task<SecretReferenceStatus> SaveAccountSecretAsync(
        string accountId,
        string rawSecret,
        CancellationToken cancellationToken = default);
    /// <summary>Explicit editor save: invalidates prior auth, preserves other settings and refuses a stale binding.</summary>
    Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret,
        string expectedProfileId, string? expectedReference, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Conditional account credential save is unavailable.");

    /// <summary>Revokes an unshared account credential. Shared references are refused without mutation; an account without a reference is a no-op.</summary>
    Task RevokeAccountSecretAsync(string accountId, CancellationToken cancellationToken = default);
}
