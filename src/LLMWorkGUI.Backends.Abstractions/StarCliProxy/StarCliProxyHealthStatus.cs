namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Result of probing a star-cliproxy instance for live health/model catalog availability
/// (ТЗ §6.11a). A non-2xx status, missing catalog or unreachable loopback endpoint is never
/// reported as healthy.
/// </summary>
public sealed record StarCliProxyHealthStatus(
    bool IsHealthy,
    string? Detail,
    DateTimeOffset CheckedAtUtc,
    int? ModelCount = null)
{
    public static StarCliProxyHealthStatus Healthy(int? modelCount, DateTimeOffset checkedAtUtc) =>
        new(true, null, checkedAtUtc, modelCount);

    public static StarCliProxyHealthStatus Unhealthy(string detail, DateTimeOffset checkedAtUtc) =>
        new(false, detail, checkedAtUtc);
}
