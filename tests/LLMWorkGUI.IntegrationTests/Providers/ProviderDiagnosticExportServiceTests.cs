using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class ProviderDiagnosticExportServiceTests
{
    private readonly ProviderDiagnosticExportService _service = new();

    [Fact]
    public async Task Export_RedactsMalformedUrlErrorsAndNestedPluginMetadata()
    {
        const string secret = "sk-proj-abcdefghijklmnopqrstuvwxyz123456";
        var settings = new CustomProviderSettings("provider", "Provider", "invalid " + secret, validateUrl: false);
        var failure = ProviderConnectionTestResult.CreateFailure(ProviderConnectionStatus.InvalidUrlFormat,
            "invalid", "Authorization: Bearer " + secret);
        var report = await _service.GenerateExportAsync(settings, failure,
            [new PluginInfo("plugin", Description: "api_key=" + secret)]);
        Assert.DoesNotContain(secret, report.ToJson());
    }

    [Fact]
    public async Task GenerateExportAsync_RedactsSecretHeadersAndApiKeys()
    {
        var headers = new[]
        {
            new CustomProviderHeader("X-Public-Client", "DesktopApp", isSecret: false),
            new CustomProviderHeader("Authorization", "Bearer sk-super-secret-key-12345", isSecret: true),
            new CustomProviderHeader("X-Api-Key", "sk-another-secret", isSecret: false) // Auto-detected as sensitive by name
        };

        var settings = new CustomProviderSettings(
            "test-provider",
            "Test Provider",
            "https://api.openai.com/v1?token=query-secret-token",
            customHeaders: headers, validateUrl: false);

        var report = await _service.GenerateExportAsync(settings);

        Assert.NotNull(report);
        Assert.Equal("test-provider", report.ProviderId);
        Assert.Equal("Test Provider", report.DisplayName);

        // Verify URL is sanitized (query string stripped)
        Assert.DoesNotContain("query-secret-token", report.SanitizedBaseUrl);
        Assert.Equal("https://api.openai.com/v1", report.SanitizedBaseUrl);

        // Verify secret headers are redacted
        var authHeader = report.SanitizedHeaders.First(h => h.Name == "Authorization");
        Assert.Equal("***REDACTED***", authHeader.Value);

        var apiKeyHeader = report.SanitizedHeaders.First(h => h.Name == "X-Api-Key");
        Assert.Equal("***REDACTED***", apiKeyHeader.Value);

        var publicHeader = report.SanitizedHeaders.First(h => h.Name == "X-Public-Client");
        Assert.Equal("DesktopApp", publicHeader.Value);

        // Verify JSON export doesn't leak secrets
        var json = report.ToJson();
        Assert.DoesNotContain("sk-super-secret-key-12345", json);
        Assert.DoesNotContain("sk-another-secret", json);
        Assert.DoesNotContain("query-secret-token", json);
        Assert.Contains("***REDACTED***", json);
    }

    [Fact]
    public async Task GenerateExportAsync_EnvironmentVariablesAreStrictlySanitized()
    {
        var settings = new CustomProviderSettings(
            "test-provider",
            "Test Provider",
            "https://api.custom.com/v1");

        var report = await _service.GenerateExportAsync(settings);

        Assert.NotNull(report.SanitizedEnvironment);

        // Ensure no environment variables containing sensitive keywords exist
        foreach (var key in report.SanitizedEnvironment.Keys)
        {
            var upper = key.ToUpperInvariant();
            Assert.DoesNotContain("KEY", upper);
            Assert.DoesNotContain("TOKEN", upper);
            Assert.DoesNotContain("SECRET", upper);
            Assert.DoesNotContain("AUTH", upper);
            Assert.DoesNotContain("PASS", upper);
            Assert.DoesNotContain("PWD", upper);
            Assert.DoesNotContain("CREDENTIAL", upper);
        }

        // Absence of every diagnostic variable must not turn this into an unconditional pass.
        foreach (var name in new[] { "OS", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 })
                Assert.True(report.SanitizedEnvironment.ContainsKey(name), $"Missing safe variable {name}.");
    }

    [Fact]
    public async Task GenerateExportAsync_DoesNotExportArbitraryDotnetEnvironment()
    {
        const string name = "DOTNET_LLMWORKGUI_REVIEW_CANARY";
        const string value = "private-environment-canary-20261004";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            var report = await _service.GenerateExportAsync(new CustomProviderSettings("test", "Test", "https://example.test/v1"));
            Assert.DoesNotContain(name, report.SanitizedEnvironment.Keys);
            Assert.DoesNotContain(value, report.ToJson());
        }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }
}
