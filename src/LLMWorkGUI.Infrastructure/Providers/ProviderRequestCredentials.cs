using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

internal static class ProviderRequestCredentials
{
    public static async Task<(string? Failure, string? Secret)> ResolveApiKeyAsync(string reference,
        string providerId, string? accountId, IProviderProfileRepository? profiles, IAccountRepository? accounts,
        ISecretLifecycleService? lifecycle, ISecretStore? store, CancellationToken cancellationToken)
    {
        if (!SecretReference.IsValid(reference) || profiles is null || lifecycle is null || store is null)
            return ("The configured API key cannot be verified in this composition.", null);
        try
        {
            var status = await lifecycle.GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);
            if (!status.IsUsable)
                return ("The configured API key is unavailable: " + status.DescribeState() + ".", null);
            if (!status.IsRegistered || status.Kind != SecretReferenceKind.ProviderApiKey)
                return ("The configured API key is unregistered or has the wrong purpose.", null);
            var bound = await profiles.GetApiKeySecretReferenceAsync(providerId, cancellationToken).ConfigureAwait(false) == reference;
            if (!bound && accountId is not null && accounts is not null)
            {
                var account = await accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
                bound = account?.ProviderProfileId == providerId && account.SecretReference == reference;
            }
            if (!bound) return ("The configured API key is not bound to the selected provider or account.", null);
            var secret = await store.GetSecretAsync(reference, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(secret) ? ("The configured API key could not be resolved.", null) : (null, secret);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("The configured API key could not be verified or resolved.", null);
        }
    }
}
