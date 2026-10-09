namespace LLMWorkGUI.Application.Providers;

public static class ProviderUrlValidator
{
    public static UrlValidationResult Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return UrlValidationResult.Failure("Base URL cannot be empty.", UrlClassification.InvalidFormat);
        }

        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return UrlValidationResult.Failure("Invalid URL format.", UrlClassification.InvalidFormat);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return UrlValidationResult.Failure("URL scheme must be http or https.", UrlClassification.InvalidFormat);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
        {
            return UrlValidationResult.Failure(
                "Base URL must not contain credentials, a query, or a fragment.", UrlClassification.InvalidFormat);
        }

        var normalized = uri.ToString();
        if (normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/');
        }

        var isLoopback = uri.IsLoopback ||
                         string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (isLoopback)
        {
            return UrlValidationResult.Success(normalized, UrlClassification.ValidLoopbackHttp);
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return UrlValidationResult.Failure(
                "Insecure HTTP is disallowed for remote hosts. Please use HTTPS.",
                UrlClassification.InsecureRemoteHttp);
        }

        return UrlValidationResult.Success(normalized, UrlClassification.ValidRemoteHttps);
    }
}
