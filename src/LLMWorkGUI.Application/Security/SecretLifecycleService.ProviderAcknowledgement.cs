using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Security;

public sealed partial class SecretLifecycleService
{
    private sealed class ProviderBindingUnknownException() : InvalidOperationException(
        "Provider credential binding is unconfirmed; refresh before another action.");

    private static async Task<long> PersistProviderProfileAsync(IProviderProfileRepository profiles,
        ProviderProfile desired, string? newKey, ProviderProfile? previous, string? previousKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await profiles.UpsertReturningRevisionAsync(desired, newKey, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.InnerException is ProviderProfileWriteConflictException)
        {
            // SQLite returned no updated revision and rolled its transaction back. A changed
            // concurrent owner is not an uncertain acknowledgement of this refused write.
            throw;
        }
        catch
        {
            ProviderProfile? observed;
            string? observedKey;
            try
            {
                // Cancellation of the original request cannot decide whether its write committed.
                observed = await profiles.GetByIdAsync(desired.Id, CancellationToken.None).ConfigureAwait(false);
                observedKey = await profiles.GetApiKeySecretReferenceAsync(desired.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch { throw new ProviderBindingUnknownException(); }

            if (observed is not null && observed.Revision is long committedRevision
                && committedRevision > (desired.Revision ?? previous?.Revision ?? -1)
                && SameProviderConfiguration(observed, desired, desired.CustomHeaders ?? previous?.CustomHeaders)
                && observedKey == (newKey ?? previousKey))
                return committedRevision;

            // Only the positively unchanged or deleted owner permits compensation. A newer or
            // unobservable owner could still reference our payloads and must retain them.
            if (observed is null && observedKey is null) throw;
            if (previous is not null && observed is not null && observed.Revision == previous.Revision
                && SameProviderConfiguration(observed, previous, previous.CustomHeaders)
                && observedKey == previousKey)
                throw;
            throw new ProviderBindingUnknownException();
        }
    }

    private static bool SameProviderConfiguration(ProviderProfile observed, ProviderProfile desired,
        IReadOnlyList<ProviderHeader>? desiredHeaders) =>
        observed.Id == desired.Id && observed.DisplayName == desired.DisplayName
        && observed.Backend == desired.Backend && observed.BaseUrl == desired.BaseUrl
        && observed.ExecutablePath == desired.ExecutablePath && observed.MaxDataClass == desired.MaxDataClass
        && observed.IsEnabled == desired.IsEnabled && observed.GatewayNativeId == desired.GatewayNativeId
        && (observed.CustomHeaders ?? []).Select(header => (header.Name, header.Value, header.SecretReference))
            .SequenceEqual((desiredHeaders ?? []).Select(header => (header.Name, header.Value, header.SecretReference)));
}
