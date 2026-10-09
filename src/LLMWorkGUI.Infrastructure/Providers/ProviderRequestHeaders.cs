using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

internal static class ProviderRequestHeaders
{
    public static async Task<string?> ApplyAsync(HttpRequestMessage request, string providerId,
        IReadOnlyList<CustomProviderHeader> headers, IProviderProfileRepository? profiles,
        ISecretLifecycleService? lifecycle, ISecretStore? store, CancellationToken cancellationToken)
    {
        try
        {
            var needsSecrets = headers.Any(h => h.SecretReference is not null);
            if (needsSecrets && (profiles is null || lifecycle is null || store is null))
                return "The saved headers cannot be verified in this composition.";
            var owner = needsSecrets ? await profiles!.GetByIdAsync(providerId, cancellationToken).ConfigureAwait(false) : null;
            foreach (var header in headers)
            {
                var value = header.Value;
                if (header.SecretReference is not null)
                {
                    if (owner?.CustomHeaders?.Any(h => string.Equals(h.Name, header.Name, StringComparison.OrdinalIgnoreCase)
                        && h.SecretReference == header.SecretReference) != true)
                        return "A saved header is not bound to this provider.";
                    var status = await lifecycle!.GetStatusAsync(header.SecretReference, cancellationToken).ConfigureAwait(false);
                    if (!status.IsUsable || !status.IsRegistered || status.Kind != SecretReferenceKind.ProviderHeader)
                        return "A saved header secret is unavailable or has the wrong purpose.";
                    value = await store!.GetSecretAsync(header.SecretReference, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(value)) return "A saved header secret could not be resolved.";
                }
                if (value.Any(c => char.IsControl(c) && c != '\t')
                    || request.Headers.Contains(header.Name)
                    || !request.Headers.TryAddWithoutValidation(header.Name, value))
                    return "A configured header is invalid or conflicts with authentication.";
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Storage and HTTP exceptions may include arbitrary secret text.
            return "The configured headers could not be prepared.";
        }
    }
}
