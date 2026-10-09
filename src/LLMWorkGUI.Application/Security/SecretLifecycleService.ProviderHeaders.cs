using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

public sealed partial class SecretLifecycleService
{
    public async Task<ProviderConfigurationSaveResult> SaveProviderConfigurationAsync(ProviderProfile profile,
        IReadOnlyList<CustomProviderHeader> headers, string? rawApiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(headers);
        // Snapshot editable input before awaiting or creating any payload.
        var edits = headers.ToArray();
        if (edits.Length > 64 || edits.Any(h => h is null)
            || edits.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edits.Length
            || edits.Any(h => h.Value.Length > 32768 || (h.IsSecret && h.SecretReference is null && string.IsNullOrWhiteSpace(h.Value))))
            throw new ArgumentException("Headers require unique names and non-empty secret values within the supported limits.", nameof(headers));
        var profiles = _providerProfiles ?? throw new InvalidOperationException("Provider storage is unavailable.");
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await profiles.GetByIdAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var revision = profile.Revision ?? previous?.Revision ?? -1;
            var apiKeyReference = await profiles.GetApiKeySecretReferenceAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var oldHeaders = previous?.CustomHeaders ?? [];
            // A reference may only be retained in its original provider and header slot. A new raw
            // value gets a fresh reference, so failed writes never change an old header's payload.
            foreach (var edit in edits.Where(h => h.SecretReference is not null))
            {
                if (!oldHeaders.Any(h => string.Equals(h.Name, edit.Name, StringComparison.OrdinalIgnoreCase)
                    && h.SecretReference == edit.SecretReference))
                    throw new InvalidOperationException("The stored header does not belong to this provider and name.");
                var status = await GetStatusCoreAsync(edit.SecretReference!, cancellationToken).ConfigureAwait(false);
                if (!status.IsUsable || status.Kind != SecretReferenceKind.ProviderHeader)
                    throw new InvalidOperationException("The stored header is unavailable; enter its value again.");
            }

            var created = new List<string>();
            var committed = false;
            long committedRevision = -1;
            ProviderProfile saved;
            try
            {
                var stored = new List<ProviderHeader>();
                foreach (var edit in edits)
                {
                    if (!edit.IsSecret) stored.Add(new(edit.Name, edit.Value));
                    else if (edit.SecretReference is not null) stored.Add(new(edit.Name, secretReference: edit.SecretReference));
                    else
                    {
                        var status = await CreateCoreAsync(edit.Value, SecretReferenceKind.ProviderHeader,
                            SecretReferenceOwnerBinding.ForProviderProfile(string.Empty, profile.Id), cancellationToken).ConfigureAwait(false);
                        created.Add(status.Reference);
                        stored.Add(new(edit.Name, secretReference: status.Reference));
                    }
                }

                saved = new ProviderProfile(profile.Id, profile.DisplayName, profile.Backend, profile.BaseUrl,
                    profile.ExecutablePath, profile.MaxDataClass, profile.IsEnabled, profile.GatewayNativeId, stored, revision);
                if (string.IsNullOrWhiteSpace(rawApiKey))
                {
                    committedRevision = await PersistProviderProfileAsync(profiles, saved, null,
                        previous, apiKeyReference, cancellationToken).ConfigureAwait(false);
                    committed = true;
                }
                else
                {
                    var keyStatus = await SaveAndPersistOwnerAsync(apiKeyReference, Normalize(rawApiKey),
                        SecretReferenceOwnerBinding.ForProviderProfile(apiKeyReference ?? string.Empty, profile.Id),
                        async reference =>
                        {
                            committedRevision = await PersistProviderProfileAsync(profiles, saved, reference,
                                previous, apiKeyReference, cancellationToken).ConfigureAwait(false);
                            committed = true;
                        }, cancellationToken).ConfigureAwait(false);
                    apiKeyReference = keyStatus.Reference;
                }
            }
            catch (Exception error)
            {
                // Never revoke headers after a durable commit, including if a subsequent status
                // lookup failed or the caller cancelled after SQLite committed.
                if (!committed && error is not ProviderBindingUnknownException)
                    foreach (var reference in created) await CompensateAsync(reference).ConfigureAwait(false);
                throw;
            }

            var retained = saved.CustomHeaders!.Select(h => h.SecretReference).ToHashSet(StringComparer.Ordinal);
            foreach (var reference in oldHeaders.Select(h => h.SecretReference).OfType<string>().Distinct())
                if (!retained.Contains(reference))
                    await RevokeSupersededAsync(reference,
                        SecretReferenceOwnerBinding.ForProviderProfile(reference, profile.Id)).ConfigureAwait(false);
            return new ProviderConfigurationSaveResult(new ProviderProfile(saved.Id, saved.DisplayName, saved.Backend,
                saved.BaseUrl, saved.ExecutablePath, saved.MaxDataClass, saved.IsEnabled, saved.GatewayNativeId,
                saved.CustomHeaders, committedRevision), apiKeyReference);
        }
        finally { _writeLock.Release(); }
    }
}
