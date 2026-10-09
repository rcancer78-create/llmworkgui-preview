using System.Diagnostics;
using System.Text.Json;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class SensitiveDataFilterTests
{
    private readonly SensitiveDataFilter _filter = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redact_LongUnmatchedIdentifierRemainsResponsiveAndMasksTrailingSecret(bool diagnosticJson)
    {
        var prefix = new string('x', 512_000);
        const string syntheticSecret = "synthetic-long-diagnostic-fixture";
        var input = prefix + "; api_key=" + syntheticSecret;
        if (diagnosticJson) input = JsonSerializer.Serialize(new { text = input });

        var timer = Stopwatch.StartNew();
        var result = diagnosticJson ? _filter.RedactDiagnostic(input) : _filter.Redact(input);
        timer.Stop();

        if (diagnosticJson)
        {
            using var document = JsonDocument.Parse(result);
            result = document.RootElement.GetProperty("text").GetString()!;
        }
        Assert.Equal(prefix + "; api_key=[REDACTED]", result);
        // Regression guard for quadratic suffix retries, with ample margin over linear processing.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Redaction took {timer.Elapsed}.");
    }

    [Fact]
    public void RedactJson_DuplicatePropertiesAndEscapedNamesPreserveJsonShape()
    {
        var result = _filter.RedactJson("{\"remaining\":7,\"remaining\":9,\"api_\\u006bey\":\"synthetic-secret\",\"api_key\":12}");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal(new[] { 7, 9 }, json.RootElement.EnumerateObject()
            .Where(property => property.Name == "remaining").Select(property => property.Value.GetInt32()));
        Assert.All(json.RootElement.EnumerateObject().Where(property => property.Name == "api_key"),
            property => Assert.Equal("[REDACTED]", property.Value.GetString()));
        Assert.DoesNotContain("synthetic-secret", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  42  ", "  42  ")]
    [InlineData(" true ", " true ")]
    [InlineData(" null ", " null ")]
    [InlineData("  \"ordinary\"  ", "ordinary")]
    public void RedactJson_DefaultScalarContractRetainsLegacyShapeAndFormatting(string input, string expected)
    {
        Assert.Equal(expected, _filter.RedactJson(input));
    }

    [Fact]
    public void Redact_MasksBearerTokenInAuthorizationHeader()
    {
        var redacted = _filter.Redact("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789");

        Assert.Equal("Authorization: [REDACTED]", redacted);
    }

    [Fact]
    public void Redact_MasksBasicAuthCredentials()
    {
        var redacted = _filter.Redact("Authorization: Basic dXNlcm5hbWU6cGFzc3dvcmQ=");

        Assert.Equal("Authorization: [REDACTED]", redacted);
    }

    [Fact]
    public void Redact_MasksBareBearerTokenPreservingScheme()
    {
        var redacted = _filter.Redact("routing header Bearer abcdefghijklmnopqrstuvwxyz");

        Assert.Equal("routing header Bearer [REDACTED]", redacted);
    }

    [Theory]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    // Deliberately invalid Slack IDs keep this redaction fixture noncredential.
    [InlineData("xoxb-SYNTHETIC-INVALID-FIXTURE")]
    [InlineData("crsr_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstu")]
    public void Redact_MasksKnownApiKeyFormats(string secret)
    {
        var redacted = _filter.Redact($"provider_key={secret}");

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_MasksPasswordsAndTokensInAssignments()
    {
        var redacted = _filter.Redact("password=hunter2;api_key=abc12345;token: abcdefgh");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc12345", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh", redacted, StringComparison.Ordinal);
        Assert.Contains("password=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("api_key=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("token: [REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_MasksSensitiveQueryParameters()
    {
        var redacted = _filter.Redact("https://api.example.com/v1?api_key=abc12345&model=gpt-4");

        Assert.DoesNotContain("abc12345", redacted, StringComparison.Ordinal);
        Assert.Contains("api_key=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("model=gpt-4", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_MasksJsonStringValues()
    {
        const string json =
            """{"provider":{"apiKey":"sk-live-abcdefghijklmnop","password":"hunter2"},"note":"hello","max_tokens":4096}""";

        var redacted = _filter.Redact(json);

        Assert.DoesNotContain("sk-live-abcdefghijklmnop", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("\"apiKey\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"hello\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":4096", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactJson_MasksSensitivePropertiesAndNestedValues()
    {
        const string json =
            """{"apiKey":"sk-live-abcdefghijklmnop","nested":{"client_secret":"xyz12345","keep":"value"},"list":["Bearer abcdefghijklmnop","plain"],"max_tokens":4096,"refresh_token":"rt-abcdefghijklmnop"}""";

        var redacted = _filter.RedactJson(json);

        Assert.DoesNotContain("sk-live-abcdefghijklmnop", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz12345", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-abcdefghijklmnop", redacted, StringComparison.Ordinal);
        Assert.Contains("\"apiKey\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"client_secret\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"refresh_token\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"value\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":4096", redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("\"plain\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactJson_FallsBackToTextRedactionForInvalidJson()
    {
        var redacted = _filter.RedactJson("Authorization: Bearer abcdefghijklmnop");

        Assert.Equal("Authorization: [REDACTED]", redacted);
    }

    [Fact]
    public void RedactJson_HandlesJsonScalarString()
    {
        Assert.Equal("Bearer [REDACTED]", _filter.RedactJson("\"Bearer abcdefghijklmnop\""));
    }

    [Fact]
    public void Redact_MasksPrivateKeyBlocks()
    {
        const string input = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----";

        Assert.Equal("[REDACTED]", _filter.Redact(input));
    }

    [Fact]
    public void Redact_LeavesNonSensitiveTextUnchanged()
    {
        const string input = "Session started for project demo with max_tokens=4096 and route OpenCode.";

        Assert.Equal(input, _filter.Redact(input));
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        const string input = "password=hunter2 apiKey=sk-live-abcdefghijklmnop";

        var once = _filter.Redact(input);
        var twice = _filter.Redact(once);

        Assert.Equal(once, twice);
        Assert.Contains("[REDACTED]", once, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsSensitiveData_DetectsSensitiveAndPlainInput()
    {
        Assert.True(_filter.ContainsSensitiveData("Authorization: Bearer abcdefghijklmnop"));
        Assert.True(_filter.ContainsSensitiveData("{\"apiKey\":\"sk-live-abcdefghijklmnop\"}"));
        Assert.False(_filter.ContainsSensitiveData("plain project notes with max_tokens=4096"));
        Assert.False(_filter.ContainsSensitiveData(null));
        Assert.False(_filter.ContainsSensitiveData(string.Empty));
    }

    [Theory]
    [InlineData("apiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("access_token", true)]
    [InlineData("Authorization", true)]
    [InlineData("client_secret", true)]
    [InlineData("password", true)]
    [InlineData("max_tokens", false)]
    [InlineData("tokenizer", false)]
    [InlineData("model", false)]
    public void IsSensitiveName_ClassifiesConfigurationKeys(string name, bool expected)
    {
        Assert.Equal(expected, SensitiveDataFilter.IsSensitiveName(name));
    }
}
