namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Connection information for one loopback star-cliproxy instance. The API key lives in memory
/// only and is never placed on a command line or written to logs (ТЗ §9.3, ADR-0007 §7).
/// </summary>
public sealed record StarCliProxyEndpoint
{
    public StarCliProxyEndpoint(Uri baseUrl, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (!baseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The star-cliproxy base URL must be absolute.", nameof(baseUrl));
        }

        if (!StarCliProxyOptions.IsLoopbackHostname(baseUrl.Host) ||
            baseUrl.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            throw new ArgumentException("The star-cliproxy endpoint must use loopback HTTP without embedded credentials.", nameof(baseUrl));
        }

        var normalized = baseUrl.AbsoluteUri.EndsWith('/')
            ? baseUrl
            : new Uri(baseUrl.AbsoluteUri + "/", UriKind.Absolute);

        BaseUrl = normalized;
        ApiKey = apiKey;
    }

    public Uri BaseUrl { get; }

    public string? ApiKey { get; }

    public override string ToString() => $"StarCliProxyEndpoint {{ Host = {BaseUrl.Host}, Port = {BaseUrl.Port}, ApiKey = <redacted> }}";

    public static StarCliProxyEndpoint Loopback(int port, string? apiKey = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        return new StarCliProxyEndpoint(new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute), apiKey);
    }
}
