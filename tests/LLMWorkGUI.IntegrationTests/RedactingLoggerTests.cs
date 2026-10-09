using LLMWorkGUI.Infrastructure.Logging;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class RedactingLoggerTests
{
    private readonly CapturingLoggerProvider _inner = new();
    private readonly RedactingLoggerProvider _provider;
    private readonly ILogger _logger;

    [Fact]
    public void Log_DoesNotForwardSecretsInsideNestedObjectsOrExceptionData()
    {
        var payload = new { Items = new[] { new { Password = "nested-canary", Name = "safe" } } };
        var error = new IOException("ordinary failure");
        error.Data["apiKey"] = "exception-canary";
        _logger.LogError(error, "Failed with {Payload}", payload);
        var entry = Assert.Single(_inner.Entries);
        Assert.DoesNotContain("nested-canary", entry.Message);
        Assert.DoesNotContain("nested-canary", System.Text.Json.JsonSerializer.Serialize(entry.State));
        Assert.Empty(entry.Exception!.Data);
    }

    public RedactingLoggerTests()
    {
        _provider = new RedactingLoggerProvider(new SensitiveDataFilter(), _inner);
        _logger = _provider.CreateLogger("RedactingLoggerTests");
    }

    [Fact]
    public void Log_RedactsBearerTokenInMessageAndArguments()
    {
        _logger.LogInformation(
            "Routing header {Header} for {User}",
            "Bearer abcdefghijklmnopqrstuvwxyz",
            "alice");

        var entry = Assert.Single(_inner.Entries);

        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", entry.Message, StringComparison.Ordinal);
        Assert.Contains("alice", entry.Message, StringComparison.Ordinal);

        var values = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(entry.State);
        var header = Assert.Single(values, pair => pair.Key == "Header");

        Assert.Equal("Bearer [REDACTED]", header.Value);
    }

    [Fact]
    public void Log_MasksValuesReferredBySensitiveKeyNames()
    {
        _logger.LogInformation("Connecting with {ApiKey}", "plain-unremarkable-value");

        var entry = Assert.Single(_inner.Entries);

        Assert.DoesNotContain("plain-unremarkable-value", entry.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry.Message, StringComparison.Ordinal);

        var values = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(entry.State);
        var apiKey = Assert.Single(values, pair => pair.Key == "ApiKey");

        Assert.Equal("[REDACTED]", apiKey.Value);
    }

    [Theory]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----")]
    public void Log_RedactsKnownSecretFormats(string secret)
    {
        _logger.LogWarning("Backend rejected payload: {Payload}", secret);

        var entry = Assert.Single(_inner.Entries);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", entry.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_RedactsPasswordsAndBasicAuth()
    {
        _logger.LogInformation("password=hunter2 basic dXNlcm5hbWU6cGFzc3dvcmQ=");

        var entry = Assert.Single(_inner.Entries);

        Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
        Assert.Contains("password=[REDACTED]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_RedactsExceptionMessages()
    {
        _logger.LogError(
            new InvalidOperationException("Authorization: Bearer abcdefghijklmnopqrstuvwxyz"),
            "Request failed");

        var entry = Assert.Single(_inner.Entries);

        var exception = Assert.IsType<RedactedException>(entry.Exception);
        Assert.Equal(nameof(InvalidOperationException), exception.OriginalExceptionType);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_PassesThroughNonSensitiveException()
    {
        var original = new InvalidOperationException("plain failure without secrets");

        _logger.LogError(original, "Request failed");

        var entry = Assert.Single(_inner.Entries);

        Assert.Same(original, entry.Exception);
    }

    [Fact]
    public void BeginScope_RedactsSensitiveScopeText()
    {
        _logger.BeginScope("password=hunter2");

        var scope = Assert.Single(_inner.Scopes);

        Assert.Equal("password=[REDACTED]", scope);
    }

    [Fact]
    public void Log_RespectsInnerLoggerEnabledState()
    {
        _inner.IsEnabled = false;

        _logger.LogInformation("password=hunter2");

        Assert.Empty(_inner.Entries);

        _inner.IsEnabled = true;

        _logger.LogInformation("password=hunter2");

        Assert.Single(_inner.Entries);
    }

    [Fact]
    public void Dispose_DisposesInnerProviderAndRejectsNewLoggers()
    {
        _provider.Dispose();
        _provider.Dispose();

        Assert.True(_inner.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => { _ = _provider.CreateLogger("after-dispose"); });
    }
}
