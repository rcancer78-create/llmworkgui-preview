namespace LLMWorkGUI.Application.Providers;

public sealed class CustomProviderSettings
{
    public CustomProviderSettings(
        string providerId,
        string displayName,
        string baseUrl,
        string? apiKeySecretRef = null,
        IReadOnlyList<CustomProviderHeader>? customHeaders = null,
        IReadOnlyList<CustomProviderModelSettings>? models = null,
        bool isEnabled = true,
        bool validateUrl = true,
        string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider ID cannot be empty.", nameof(providerId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        }

        if (validateUrl)
        {
            var urlResult = ProviderUrlValidator.Validate(baseUrl);
            if (!urlResult.IsValid)
            {
                throw new ArgumentException($"Invalid Base URL: {urlResult.ErrorMessage}", nameof(baseUrl));
            }

            BaseUrl = urlResult.NormalizedUrl!;
        }
        else
        {
            BaseUrl = baseUrl?.Trim() ?? string.Empty;
        }

        ProviderId = providerId.Trim();
        DisplayName = displayName.Trim();
        ApiKeySecretRef = apiKeySecretRef?.Trim();
        var headers = customHeaders?.ToArray() ?? [];
        if (headers.Length > 64 || headers.Any(h => h is null)
            || headers.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new ArgumentException("Headers must have unique names and contain at most 64 entries.", nameof(customHeaders));
        CustomHeaders = Array.AsReadOnly(headers);
        Models = models ?? Array.Empty<CustomProviderModelSettings>();
        IsEnabled = isEnabled;
        AccountId = accountId;
    }

    public string ProviderId { get; }

    public string DisplayName { get; }

    public string BaseUrl { get; }

    public string? ApiKeySecretRef { get; }

    public IReadOnlyList<CustomProviderHeader> CustomHeaders { get; }

    public IReadOnlyList<CustomProviderModelSettings> Models { get; }

    public bool IsEnabled { get; }
    public string? AccountId { get; }
}
