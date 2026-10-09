using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// Applies the sensitive-data filter to a candidate backend model id and then the model-id policy, so
/// the same value cannot pass as a model when it is really a redacted placeholder, a key or a path.
/// </summary>
internal static class SanitizedModelId
{
    public static bool TryNormalize(SensitiveDataFilter sensitiveDataFilter, string? value, out string modelId)
    {
        ArgumentNullException.ThrowIfNull(sensitiveDataFilter);

        modelId = string.Empty;

        var redacted = sensitiveDataFilter.Redact(value);

        // A fully redacted value is not a model id: the placeholder must never reach a request.
        if (string.Equals(redacted, SensitiveDataFilter.Placeholder, StringComparison.Ordinal))
        {
            return false;
        }

        return BackendModelIdPolicy.TryNormalize(redacted, out modelId);
    }
}
