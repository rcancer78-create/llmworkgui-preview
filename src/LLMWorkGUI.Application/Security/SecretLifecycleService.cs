using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.Application.Security;

/// <summary>
/// Implements the ADR-0005 §5 lifecycle on top of an <see cref="ISecretStore"/> and the
/// reference-only metadata table.
///
/// Two invariants drive the ordering of every operation:
///
/// <list type="number">
/// <item>A value is written before the metadata claims it exists, so a crash can only leave a
/// reference that resolves to nothing (reported as <c>Missing</c>) and never a reported rotation
/// without a payload behind it.</item>
/// <item>A revocation is recorded before the payload is deleted, so a surviving file can never make
/// a revoked reference usable again.</item>
/// </list>
///
/// Secret values are passed through and never logged, formatted into messages or persisted: only the
/// URN appears in a log record or a compensation message.
/// </summary>
public sealed partial class SecretLifecycleService : ISecretLifecycleService, IDisposable
{
    private readonly ISecretStore _secretStore;
    private readonly ISecretReferenceRepository _references;
    private readonly IProviderProfileRepository? _providerProfiles;
    private readonly IAccountRepository? _accounts;
    private readonly IProviderDeletionStore? _providerDeletion;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecretLifecycleService>? _logger;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    // Payload rotation and profile CAS must be serialized even when a composition creates more
    // than one lifecycle instance. The application supervisor separately excludes other processes.
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// The outcome of a rotation. <see cref="SupersededReference"/> is set only when the store could
    /// not overwrite the payload in place and the value therefore moved to a new URN; the caller that
    /// has to persist that URN first decides when the old one is revoked.
    /// </summary>
    private readonly record struct RotationOutcome(SecretReferenceStatus Status, string? SupersededReference);

    /// <summary>The reference a save now points at, plus the reference it superseded, if any.</summary>

    public SecretLifecycleService(
        ISecretStore secretStore,
        ISecretReferenceRepository references,
        IProviderProfileRepository? providerProfiles = null,
        IAccountRepository? accounts = null,
        TimeProvider? timeProvider = null,
        ILogger<SecretLifecycleService>? logger = null,
        IApplicationInstanceGuard? instanceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(references);

        _secretStore = secretStore;
        _references = references;
        _providerProfiles = providerProfiles;
        _accounts = accounts;
        _providerDeletion = providerProfiles as IProviderDeletionStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _instanceGuard = instanceGuard;
    }

    private async Task EnterWriteAsync(CancellationToken cancellationToken)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
        }
        catch
        {
            _writeLock.Release();
            throw;
        }
    }

    public void Dispose() { /* The process, rather than an individual service, owns the shared gate. */ }

    public async Task<SecretReferenceStatus> GetStatusAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        return await GetStatusCoreAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecretReferenceStatus> CreateAsync(
        string rawSecret,
        SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey,
        SecretReferenceOwnerBinding? owner = null,
        CancellationToken cancellationToken = default)
    {
        var secret = Normalize(rawSecret);

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateCoreAsync(secret, kind, owner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SecretReferenceStatus> RotateAsync(
        string reference,
        string rawSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var secret = Normalize(rawSecret);

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await RotateCoreAsync(reference, secret, cancellationToken).ConfigureAwait(false);

            if (outcome.SupersededReference is not null)
            {
                // A standalone rotation has no binding step to wait for, so the superseded reference
                // is revoked here. The caller of a save defers the same work until the new reference
                // is durably stored.
                try
                {
                    await RevokeCoreAsync(outcome.SupersededReference, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // This replacement has never been returned or bound. Retire it with the
                    // existing cancellation-independent compensation; preserve the primary
                    // superseded-reference failure even if compensation/diagnostics fail.
                    try { await CompensateAsync(outcome.Status.Reference).ConfigureAwait(false); }
                    catch (Exception) { }
                    throw;
                }
            }

            return outcome.Status;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SecretReferenceStatus> RevokeAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RevokeCoreAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SecretReferenceStatus> BindAsync(
        string reference,
        SecretReferenceOwnerBinding owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(owner);

        if (!SecretReference.IsValid(reference))
        {
            throw new ArgumentException(
                $"Secret reference must match the canonical form '{SecretReference.Prefix}<identifier>'.",
                nameof(reference));
        }

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var binding = new SecretReferenceOwnerBinding
            {
                Reference = reference,
                OwnerKind = owner.OwnerKind,
                OwnerId = owner.OwnerId
            };

            await _references.AddOwnerAsync(binding, cancellationToken).ConfigureAwait(false);
            return await GetStatusCoreAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SecretReferenceStatus> SaveProviderApiKeyAsync(
        ProviderProfile profile,
        string rawSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var profiles = _providerProfiles
            ?? throw new InvalidOperationException(
                "No provider profile repository is available, so a provider API key cannot be bound.");
        var secret = Normalize(rawSecret);

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var priorProfile = await profiles.GetByIdAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var guardedProfile = new ProviderProfile(profile.Id, profile.DisplayName, profile.Backend, profile.BaseUrl,
                profile.ExecutablePath, profile.MaxDataClass, profile.IsEnabled, profile.GatewayNativeId,
                profile.CustomHeaders, profile.Revision ?? priorProfile?.Revision ?? -1);
            var current = await profiles
                .GetApiKeySecretReferenceAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);

            return await SaveAndPersistOwnerAsync(current, secret,
                SecretReferenceOwnerBinding.ForProviderProfile(current ?? string.Empty, profile.Id),
                async reference => _ = await PersistProviderProfileAsync(profiles, guardedProfile, reference,
                    priorProfile, current, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RevokeProviderApiKeyAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerProfileId);

        var profiles = _providerProfiles
            ?? throw new InvalidOperationException(
                "No provider profile repository is available, so a provider API key cannot be revoked.");

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The bound reference is read inside the lock: a sample taken before it could revoke an
            // already replaced reference and leave the one the profile actually points at usable.
            var current = await profiles
                .GetApiKeySecretReferenceAsync(providerProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(current))
            {
                return;
            }

            if (!await RevokeSupersededAsync(current,
                SecretReferenceOwnerBinding.ForProviderProfile(current, providerProfileId),
                suppressFailure: false, retireAfterCommit: false).ConfigureAwait(false))
                throw new InvalidOperationException("The provider credential remains in use by another owner and cannot be revoked.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<SecretReferenceStatus> SaveAccountSecretAsync(
        string accountId,
        string rawSecret,
        CancellationToken cancellationToken = default) => SaveAccountSecretCoreAsync(accountId, rawSecret, null, null, cancellationToken);

    public Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret,
        string expectedProfileId, string? expectedReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileId);
        return SaveAccountSecretCoreAsync(accountId, rawSecret, expectedProfileId, expectedReference, cancellationToken);
    }

    private async Task<SecretReferenceStatus> SaveAccountSecretCoreAsync(string accountId, string rawSecret,
        string? expectedProfileId, string? expectedReference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var accounts = _accounts
            ?? throw new InvalidOperationException(
                "No account repository is available, so an account credential cannot be bound.");
        var secret = Normalize(rawSecret);

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The account is read inside the lock. Two overlapping saves that both sampled the
            // pre-lock reference would each mint a replacement and the second would write its older
            // snapshot back, leaving the first replacement active with a payload the account no
            // longer uses.
            var account = await accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Account '{accountId}' was not found.");
            var current = account.SecretReference;
            if (expectedProfileId is not null && (account.ProviderProfileId != expectedProfileId || current != expectedReference))
                throw new InvalidOperationException("Account credential settings changed; refresh the editor.");

            return await SaveAndPersistOwnerAsync(current, secret,
                SecretReferenceOwnerBinding.ForAccount(current ?? string.Empty, accountId),
                reference => expectedProfileId is null
                    ? accounts.SaveAsync(account.WithSecretReference(reference), cancellationToken)
                    : PersistConditionalAccountKeyAsync(accounts, accountId, expectedProfileId, expectedReference, reference, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RevokeAccountSecretAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var accounts = _accounts
            ?? throw new InvalidOperationException(
                "No account repository is available, so an account credential cannot be revoked.");

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
            var current = account?.SecretReference;

            if (string.IsNullOrWhiteSpace(current))
            {
                return;
            }

            if (!await RevokeSupersededAsync(current,
                    SecretReferenceOwnerBinding.ForAccount(current, accountId),
                    suppressFailure: false, retireAfterCommit: false).ConfigureAwait(false))
                throw new InvalidOperationException("The account credential is shared; replace or detach the other bindings before revoking it.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class AccountKeyBindingUnknownException() : InvalidOperationException("Account key binding is unconfirmed; refresh before another action.");

    private static async Task PersistConditionalAccountKeyAsync(IAccountRepository accounts, string accountId,
        string profileId, string? expectedReference, string reference, CancellationToken token)
    {
        try { await accounts.ReplaceSecretReferenceAsync(accountId, profileId, expectedReference, reference, token).ConfigureAwait(false); }
        catch
        {
            Account? observed;
            try { observed = await accounts.GetByIdAsync(accountId, CancellationToken.None).ConfigureAwait(false); }
            catch { throw new AccountKeyBindingUnknownException(); }
            // Re-observe an uncertain commit acknowledgement. Never revoke a newly bound key as compensation.
            if (observed?.ProviderProfileId == profileId && observed.SecretReference == reference) return;
            if (observed is not null && (observed.ProviderProfileId != profileId || observed.SecretReference != expectedReference))
                throw new AccountKeyBindingUnknownException();
            throw; // Confirmed unchanged/deleted owner: compensate the newly minted reference only.
        }
    }

    private static string Normalize(string rawSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawSecret);

        var secret = rawSecret.Trim();

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(rawSecret));
        }

        return secret;
    }

    private async Task<SecretReferenceStatus> CreateCoreAsync(
        string secret,
        SecretReferenceKind kind,
        SecretReferenceOwnerBinding? owner,
        CancellationToken cancellationToken)
    {
        var reference = await _secretStore.SaveSecretAsync(secret, cancellationToken).ConfigureAwait(false);

        try
        {
            await _references.InsertAsync(
                new SecretReferenceMetadata
                {
                    Reference = reference,
                    Kind = kind,
                    State = SecretReferenceState.Active,
                    CreatedAtUtc = _timeProvider.GetUtcNow()
                },
                cancellationToken).ConfigureAwait(false);

            if (owner is not null)
            {
                await _references.AddOwnerAsync(
                    new SecretReferenceOwnerBinding
                    {
                        Reference = reference,
                        OwnerKind = owner.OwnerKind,
                        OwnerId = owner.OwnerId
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            return await GetStatusCoreAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A payload without metadata would be invisible to the lifecycle, so it is removed
            // instead of being left behind untracked.
            await CompensateAsync(reference).ConfigureAwait(false);
            throw;
        }

    }

    private async Task<RotationOutcome> RotateCoreAsync(
        string reference,
        string secret,
        CancellationToken cancellationToken)
    {
        if (!SecretReference.IsValid(reference))
        {
            throw new ArgumentException(
                $"Secret reference must match the canonical form '{SecretReference.Prefix}<identifier>'.",
                nameof(reference));
        }

        var metadata = await _references.GetAsync(reference, cancellationToken).ConfigureAwait(false);

        if (metadata is { IsRevoked: true })
        {
            throw new InvalidOperationException(
                $"Secret reference '{reference}' was revoked and cannot be rotated; a new reference is required.");
        }

        if (_secretStore is not ISecretPayloadManager payloadManager)
        {
            // The store cannot overwrite a payload in place, so the value moves to a new reference.
            // Reporting a same-reference rotation here would be false. The superseded reference is
            // returned instead of being revoked right away: a caller that still has to store the new
            // reference would otherwise destroy the working key if that write fails.
            var replacement = await CreateCoreAsync(
                    secret,
                    metadata?.Kind ?? SecretReferenceKind.ProviderApiKey,
                    owner: null,
                    cancellationToken)
                .ConfigureAwait(false);

            return new RotationOutcome(replacement, reference);
        }

        // Payload first, metadata second: a metadata failure leaves an Active reference whose
        // payload is already the new value, which is still a coherent state to report.
        await payloadManager.OverwriteSecretAsync(reference, secret, cancellationToken).ConfigureAwait(false);

        var rotatedAt = _timeProvider.GetUtcNow();

        if (metadata is null)
        {
            // A reference that predates the metadata table is registered by its first rotation, so
            // it never stays invisible to the lifecycle.
            await _references.InsertAsync(
                new SecretReferenceMetadata
                {
                    Reference = reference,
                    Kind = SecretReferenceKind.ProviderApiKey,
                    State = SecretReferenceState.Active,
                    CreatedAtUtc = rotatedAt,
                    LastRotatedAtUtc = rotatedAt
                },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _references.MarkRotatedAsync(reference, rotatedAt, cancellationToken).ConfigureAwait(false);

            if (metadata.State != SecretReferenceState.Active)
            {
                await _references
                    .UpdateStateAsync(reference, SecretReferenceState.Active, revokedAtUtc: null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new RotationOutcome(
            await GetStatusCoreAsync(reference, cancellationToken).ConfigureAwait(false),
            SupersededReference: null);
    }

    private async Task<SecretReferenceStatus> RevokeCoreAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        var metadata = await _references.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        var revokedAt = _timeProvider.GetUtcNow();

        if (metadata is null)
        {
            // A reference without metadata is still revoked explicitly, otherwise a legacy payload
            // would keep working with nothing in the database to say so.
            if (SecretReference.IsValid(reference))
            {
                await _references.InsertAsync(
                    new SecretReferenceMetadata
                    {
                        Reference = reference,
                        Kind = SecretReferenceKind.Unspecified,
                        State = SecretReferenceState.Revoked,
                        CreatedAtUtc = revokedAt,
                        RevokedAtUtc = revokedAt
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _references
                .UpdateStateAsync(reference, SecretReferenceState.Revoked, revokedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        if (SecretReference.IsValid(reference))
        {
            await _secretStore.DeleteSecretAsync(reference, cancellationToken).ConfigureAwait(false);
            if (_providerDeletion is not null)
                await _providerDeletion.CompleteSecretDeletionAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }

        return await GetStatusCoreAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SecretReferenceStatus> GetStatusCoreAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        var metadata = await _references.GetAsync(reference, cancellationToken).ConfigureAwait(false);

        if (metadata is { IsRevoked: true })
        {
            return SecretReferenceStatus.Revoked(
                reference,
                metadata.Kind,
                isRegistered: true,
                createdAtUtc: metadata.CreatedAtUtc);
        }

        if (metadata?.State == SecretReferenceState.Missing)
            return SecretReferenceStatus.Missing(reference, metadata.Kind, isRegistered: true);

        if (!SecretReference.IsValid(reference))
        {
            // A value the application would reject can never be resolved, so it fails closed. This
            // also keeps a legacy row that the migration deliberately skipped out of the send path.
            return SecretReferenceStatus.Missing(reference, metadata?.Kind ?? SecretReferenceKind.Unspecified, metadata is not null);
        }

        if (!await PayloadExistsAsync(reference, cancellationToken).ConfigureAwait(false))
        {
            return SecretReferenceStatus.Missing(
                reference,
                metadata?.Kind ?? SecretReferenceKind.Unspecified,
                metadata is not null);
        }

        return SecretReferenceStatus.Active(
            reference,
            metadata?.Kind ?? SecretReferenceKind.Unspecified,
            isRegistered: metadata is not null,
            createdAtUtc: metadata?.CreatedAtUtc,
            lastRotatedAtUtc: metadata?.LastRotatedAtUtc);
    }

    private async Task<SecretReferenceStatus> SaveAndPersistOwnerAsync(
        string? current,
        string secret,
        SecretReferenceOwnerBinding owner,
        Func<string, Task> persistOwner,
        CancellationToken cancellationToken)
    {
        // A bound save changes two stores. Keep the old payload untouched until the owner
        // commit succeeds; neither owner failure nor a failed cleanup needs to restore it.
        var previous = current is null ? null : await _references.GetAsync(current, cancellationToken).ConfigureAwait(false);
        var saved = await CreateCoreAsync(secret, SecretReferenceKind.ProviderApiKey, owner, cancellationToken).ConfigureAwait(false);
        try
        {
            await persistOwner(saved.Reference).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not AccountKeyBindingUnknownException and not ProviderBindingUnknownException)
        {
            await CompensateAsync(saved.Reference).ConfigureAwait(false);
            throw;
        }

        // Wrong-purpose/legacy values are never reclaimed as a side effect of entering a new key.
        // Retirement is after commit and preserves actual other bindings, including incomplete owners.
        if (previous is { Kind: SecretReferenceKind.ProviderApiKey } && SecretReference.IsValid(current))
            await RevokeSupersededAsync(current!, owner).ConfigureAwait(false);
        return saved;
    }

    /// <summary>
    /// Revokes a reference that the caller has already stopped pointing at. It runs after the new
    /// reference is durably stored and ignores cancellation, because the binding is committed at that
    /// point: failing the save here would report a failure for work that actually succeeded.
    /// Revocation can run only when every other recorded owner has also stopped using it. An
    /// unavailable owner repository retains the old reference conservatively.
    /// </summary>
    private async Task<bool> RevokeSupersededAsync(string reference, SecretReferenceOwnerBinding movedOwner,
        bool suppressFailure = true, bool retireAfterCommit = true)
    {
        try
        {
            if (_providerDeletion is not null && retireAfterCommit)
            {
                // SQLite decides retirement from all real bindings in the owner's transaction.
                // Absence from the queue may mean an unrecorded/shared owner still uses this URN.
                if ((await _providerDeletion.ListPendingSecretDeletionsAsync(CancellationToken.None).ConfigureAwait(false))
                    .Contains(reference, StringComparer.Ordinal))
                {
                    await _secretStore.DeleteSecretAsync(reference, CancellationToken.None).ConfigureAwait(false);
                    await _providerDeletion.CompleteSecretDeletionAsync(reference, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }
                return false;
            }
            if (_accounts is not null && (await _accounts.ListAllAsync(CancellationToken.None).ConfigureAwait(false))
                .Any(account => account.SecretReference == reference
                    && (movedOwner.OwnerKind != SecretReferenceOwnerKind.Account || account.Id != movedOwner.OwnerId))) return false;
            if (_providerProfiles is not null)
            {
                // Actual persisted uses are authoritative even if historical/imported metadata
                // omitted an owner row. Never revoke a sibling profile's surviving header/key.
                foreach (var profile in await _providerProfiles.ListAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (profile.CustomHeaders?.Any(h => h.SecretReference == reference) == true
                        || ((movedOwner.OwnerKind != SecretReferenceOwnerKind.ProviderProfile || profile.Id != movedOwner.OwnerId)
                            && await _providerProfiles.GetApiKeySecretReferenceAsync(profile.Id, CancellationToken.None).ConfigureAwait(false) == reference))
                        return false;
                }
            }
            foreach (var owner in await _references.ListOwnersAsync(reference, CancellationToken.None).ConfigureAwait(false))
            {
                if (owner.OwnerKind == movedOwner.OwnerKind && owner.OwnerId == movedOwner.OwnerId)
                {
                    continue;
                }

                var currentReference = owner.OwnerKind switch
                {
                    SecretReferenceOwnerKind.ProviderProfile when _providerProfiles is not null =>
                        await _providerProfiles.GetApiKeySecretReferenceAsync(owner.OwnerId, CancellationToken.None).ConfigureAwait(false),
                    SecretReferenceOwnerKind.Account when _accounts is not null =>
                        await _accounts.GetSecretReferenceAsync(owner.OwnerId, CancellationToken.None).ConfigureAwait(false),
                    _ => reference
                };
                if (currentReference == reference)
                {
                    return false;
                }
                if (owner.OwnerKind == SecretReferenceOwnerKind.ProviderProfile && _providerProfiles is not null
                    && (await _providerProfiles.GetByIdAsync(owner.OwnerId, CancellationToken.None).ConfigureAwait(false))
                        ?.CustomHeaders?.Any(h => h.SecretReference == reference) == true)
                    return false;
            }

            await RevokeCoreAsync(reference, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (suppressFailure)
        {
            _logger?.LogWarning(
                "Could not revoke the superseded secret reference {SecretReference} after its replacement was stored ({FailureType}).",
                reference, exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> PayloadExistsAsync(string reference, CancellationToken cancellationToken)
    {
        // The original store contract can resolve presence even without the optional inspection
        // capability. Use the resolved value only for this Boolean; never expose it in status/logs.
        return _secretStore is ISecretPayloadManager payloadManager
            ? await payloadManager.PayloadExistsAsync(reference, cancellationToken).ConfigureAwait(false)
            : await _secretStore.GetSecretAsync(reference, cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task TryDeletePayloadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!SecretReference.IsValid(reference))
        {
            return;
        }

        try
        {
            await _secretStore.DeleteSecretAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(
                "Could not remove the payload of secret reference {SecretReference}; it remains revoked ({FailureType}).",
                reference, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Best-effort cleanup of a reference that was created but never bound. The payload is removed and
    /// the row is left in the <c>Revoked</c> state instead of being deleted, so a reference can never
    /// exist in a state that would allow its use. Both steps are independent: a metadata store that
    /// refuses the revocation still must not leave a readable payload behind, because an unregistered
    /// reference without a payload is reported as <c>Missing</c> and is unusable as well.
    /// </summary>
    private async Task CompensateAsync(string reference)
    {
        try
        {
            await RevokeCoreAsync(reference, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                "Could not record revocation of unbound secret reference {SecretReference}; attempting payload removal ({FailureType}).",
                reference, exception.GetType().Name);
        }

        if (!SecretReference.IsValid(reference))
        {
            return;
        }

        try
        {
            await _secretStore.DeleteSecretAsync(reference, CancellationToken.None).ConfigureAwait(false);
            if (_providerDeletion is not null)
                await _providerDeletion.CompleteSecretDeletionAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                "Could not remove the unbound secret reference {SecretReference} during compensation ({FailureType}).",
                reference, exception.GetType().Name);
        }
    }
}
