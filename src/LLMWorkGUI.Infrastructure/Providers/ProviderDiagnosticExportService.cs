using System.Collections;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Generates redacted diagnostic packages for provider troubleshooting,
/// guaranteeing zero leakage of API credentials, secret headers, or sensitive environment variables.
/// </summary>
public sealed class ProviderDiagnosticExportService : IProviderDiagnosticExportService
{
    private static readonly string[] s_sensitiveKeywords =
    [
        "KEY", "TOKEN", "SECRET", "AUTH", "PASS", "PWD", "CREDENTIAL", "COOKIE", "SIGNATURE", "CERT", "PRIVATE"
    ];

    public Task<ProviderDiagnosticReport> GenerateExportAsync(
        CustomProviderSettings settings,
        ProviderConnectionTestResult? connectionResult = null,
        IReadOnlyList<PluginInfo>? plugins = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var filter = new SensitiveDataFilter();

        // 1. Sanitize Base URL
        var sanitizedUrl = SanitizeUrl(settings.BaseUrl);

        // 2. Sanitize Custom Headers (strict masking)
        var sanitizedHeaders = new List<CustomProviderHeader>();
        if (settings.CustomHeaders != null)
        {
            foreach (var h in settings.CustomHeaders)
            {
                var isSensitive = h.IsSecret || IsHeaderNameSensitive(h.Name);
                var maskedValue = isSensitive ? "***REDACTED***" : filter.RedactDiagnostic(h.Value);
                sanitizedHeaders.Add(new CustomProviderHeader(h.Name, maskedValue, isSecret: isSensitive));
            }
        }

        // 3. Extract Discovered Models
        var models = new List<DiscoveredModelDetails>();
        if (connectionResult != null && connectionResult.DiscoveredModels.Count > 0)
        {
            foreach (var m in connectionResult.DiscoveredModels)
            {
                models.Add(ModelCapabilityDetector.DetectCapabilities(m.Id, m.Name, m.OwnedBy));
            }
        }
        else if (settings.Models != null && settings.Models.Count > 0)
        {
            foreach (var m in settings.Models)
            {
                models.Add(ModelCapabilityDetector.DetectCapabilities(m.ModelId, m.DisplayName));
            }
        }

        // 4. Sanitize Environment Variables
        var sanitizedEnv = CollectSafeEnvironmentVariables();

        var report = new ProviderDiagnosticReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ProviderId = filter.RedactDiagnostic(settings.ProviderId),
            DisplayName = filter.RedactDiagnostic(settings.DisplayName),
            SanitizedBaseUrl = sanitizedUrl,
            SanitizedHeaders = sanitizedHeaders,
            LastConnectionStatus = connectionResult?.Status,
            LastStatusCode = connectionResult?.StatusCode,
            LastLatencyMs = connectionResult?.LatencyMs,
            LastErrorMessage = connectionResult?.ErrorMessage is { } error ? filter.RedactDiagnostic(error) : null,
            DiscoveredModels = models.Select(model => model with
            {
                Id = filter.RedactDiagnostic(model.Id), Name = filter.RedactDiagnostic(model.Name),
                Description = model.Description is { } description ? filter.RedactDiagnostic(description) : null,
                SupportedReasoningEfforts = model.SupportedReasoningEfforts.Select(filter.RedactDiagnostic).ToArray()
            }).ToArray(),
            ActivePlugins = (plugins ?? Array.Empty<PluginInfo>()).Select(plugin => plugin with
            {
                Name = filter.RedactDiagnostic(plugin.Name),
                Version = plugin.Version is { } version ? filter.RedactDiagnostic(version) : null,
                Description = plugin.Description is { } description ? filter.RedactDiagnostic(description) : null
            }).ToArray(),
            SanitizedEnvironment = sanitizedEnv.ToDictionary(pair => pair.Key, pair => filter.RedactDiagnostic(pair.Value))
        };

        return Task.FromResult(report);
    }

    private static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return SensitiveDataFilter.Placeholder;
        }
    }

    private static bool IsHeaderNameSensitive(string name)
    {
        return CredentialTextRedactor.IsSensitiveHeaderName(name);
    }

    private static Dictionary<string, string> CollectSafeEnvironmentVariables()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            var val = entry.Value?.ToString();

            if (string.IsNullOrWhiteSpace(key) || val == null) continue;

            // Reject any variable containing sensitive terms in the name
            if (IsSensitiveEnvVarName(key))
            {
                continue;
            }

            // Only whitelist common safe system diagnostic variables
            if (IsSafeDiagnosticVariable(key))
            {
                result[key] = val;
            }
        }

        return result;
    }

    private static bool IsSensitiveEnvVarName(string key)
    {
        foreach (var keyword in s_sensitiveKeywords)
        {
            if (key.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeDiagnosticVariable(string key)
    {
        var upper = key.ToUpperInvariant();
        return upper is "OS" or "PROCESSOR_ARCHITECTURE" or "PROCESSOR_IDENTIFIER" or "NUMBER_OF_PROCESSORS"
            or "SYSTEMROOT" or "TEMP" or "TMP";
    }
}
