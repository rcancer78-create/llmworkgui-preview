namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Detailed result of a provider connection health test.
/// </summary>
public sealed record ProviderConnectionTestResult
{
    public required ProviderConnectionStatus Status { get; init; }
    public int? StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DiscoveredModelInfo> DiscoveredModels { get; init; } = Array.Empty<DiscoveredModelInfo>();
    public string SanitizedEndpointUrl { get; init; } = string.Empty;
    public bool IsSuccessful => Status == ProviderConnectionStatus.Success;

    public static ProviderConnectionTestResult CreateSuccess(
        string sanitizedUrl,
        long latencyMs,
        IReadOnlyList<DiscoveredModelInfo> models,
        int statusCode = 200) =>
        new()
        {
            Status = ProviderConnectionStatus.Success,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            SanitizedEndpointUrl = sanitizedUrl,
            DiscoveredModels = models
        };

    public static ProviderConnectionTestResult CreateFailure(
        ProviderConnectionStatus status,
        string sanitizedUrl,
        string errorMessage,
        int? statusCode = null,
        long latencyMs = 0) =>
        new()
        {
            Status = status,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            SanitizedEndpointUrl = sanitizedUrl,
            ErrorMessage = errorMessage
        };
}
