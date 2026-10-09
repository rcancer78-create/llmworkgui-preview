using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Redacted, exportable diagnostic package for troubleshooting provider connections,
/// ensuring zero secret token leakage.
/// </summary>
public sealed record ProviderDiagnosticReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string SanitizedBaseUrl { get; init; }
    public IReadOnlyList<CustomProviderHeader> SanitizedHeaders { get; init; } = Array.Empty<CustomProviderHeader>();
    public ProviderConnectionStatus? LastConnectionStatus { get; init; }
    public int? LastStatusCode { get; init; }
    public long? LastLatencyMs { get; init; }
    public string? LastErrorMessage { get; init; }
    public IReadOnlyList<DiscoveredModelDetails> DiscoveredModels { get; init; } = Array.Empty<DiscoveredModelDetails>();
    public IReadOnlyList<PluginInfo> ActivePlugins { get; init; } = Array.Empty<PluginInfo>();
    public IReadOnlyDictionary<string, string> SanitizedEnvironment { get; init; } = new Dictionary<string, string>();

    public string ToJson(bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(this, options);
    }
}
