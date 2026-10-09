using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class ProviderConnectionTestServiceTests
{
    [Fact]
    public async Task QueryCredential_IsRejectedAtServiceBoundaryBeforeNetwork()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();
        var settings = new CustomProviderSettings("local", "Local", server.BaseUrl + "?api_key=canary", validateUrl: false);
        var result = await new ProviderConnectionTestService().TestConnectionAsync(settings);
        Assert.Equal(ProviderConnectionStatus.InvalidUrlFormat, result.Status);
    }
    private class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new();

        public void SetSecret(string reference, string secret) => _secrets[reference] = secret;

        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"urn:secret:dpapi:{Guid.NewGuid():N}";
            _secrets[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            _secrets.TryGetValue(secretReference, out var val);
            return Task.FromResult(val);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.Remove(secretReference));
        }
    }

    [Fact]
    public async Task TestConnectionAsync_Successful_ReturnsDiscoveredModelsAndSuccessStatus()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("local-mock", "Local Mock", server.BaseUrl);

        var result = await service.TestConnectionAsync(settings);

        Assert.True(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.Success, result.Status);
        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(result.DiscoveredModels);
        Assert.Contains(result.DiscoveredModels, m => m.Id == "mock-gpt-4o");
        Assert.True(result.LatencyMs >= 0);
    }

    [Fact]
    public async Task TestConnectionAsync_InsecureRemoteHttp_FailsImmediatelyWithoutNetwork()
    {
        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("remote-insecure", "Remote Insecure", "http://external-ai-host.com/v1", validateUrl: false);

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.InsecureRemoteHttp, result.Status);
        Assert.Contains("Insecure", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task TestConnectionAsync_InvalidUrl_FailsImmediatelyWithInvalidUrlFormat()
    {
        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("invalid", "Invalid", "not-a-valid-url", validateUrl: false);

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.InvalidUrlFormat, result.Status);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerRequiresApiKey_FailsWithAuthErrorIfWrongKey()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "secret-super-key-999";
        await server.StartAsync();

        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("auth-mock", "Auth Mock", server.BaseUrl);

        // 1. Without key -> 401 AuthenticationFailed
        var resultNoKey = await service.TestConnectionAsync(settings);
        Assert.False(resultNoKey.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.AuthenticationFailed, resultNoKey.Status);
        Assert.Equal(401, resultNoKey.StatusCode);

        // 2. With wrong key -> 401 AuthenticationFailed
        var resultWrongKey = await service.TestConnectionAsync(settings, explicitApiKey: "wrong-key");
        Assert.False(resultWrongKey.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.AuthenticationFailed, resultWrongKey.Status);

        // 3. With correct key -> Success
        var resultValidKey = await service.TestConnectionAsync(settings, explicitApiKey: "secret-super-key-999");
        Assert.True(resultValidKey.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.Success, resultValidKey.Status);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerReturns500_ReturnsRemoteServerError()
    {
        await using var server = new LocalMockAiServer();
        server.SimulatedStatusCode = 500;
        await server.StartAsync();

        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("error-mock", "Error Mock", server.BaseUrl);

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.RemoteServerError, result.Status);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerReturns404_ReturnsEndpointNotFound()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var service = new ProviderConnectionTestService();
        // Base URL pointing to a path that does not implement /models
        var wrongBaseUrl = $"http://127.0.0.1:{server.Port}/unknown-path";
        var settings = new CustomProviderSettings("not-found", "Not Found", wrongBaseUrl);

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.EndpointNotFound, result.Status);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenPortClosed_ReturnsConnectionRefused()
    {
        var service = new ProviderConnectionTestService();
        // Port 59999 is typically closed on loopback
        var settings = new CustomProviderSettings("closed-mock", "Closed Mock", "http://127.0.0.1:59999/v1");

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.ConnectionRefused, result.Status);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerTimesOut_ReturnsTimedOut()
    {
        await using var server = new LocalMockAiServer();

        // The server delay must dominate the client timeout by a wide margin. With a narrow margin
        // (for example 500 ms versus 50 ms) the CancelAfter timer callback can be starved while the
        // whole solution runs its assemblies in parallel, the delayed response then wins the race and
        // the call reports success instead of a timeout. The delay is never actually awaited by the
        // test: the client gives up after its own timeout, so the case still completes in
        // milliseconds.
        server.SimulatedDelay = TimeSpan.FromSeconds(30);
        await server.StartAsync();

        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings("timeout-mock", "Timeout Mock", server.BaseUrl);

        var result = await service.TestConnectionAsync(settings, timeout: TimeSpan.FromMilliseconds(250));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task TestConnectionAsync_WithSecretStore_ResolvesKeyAndPassesAuth()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-dpapi-stored-secret-123";
        await server.StartAsync();

        var secretStore = new FakeSecretStore();
        var secretRef = "urn:llmworkgui:secret:test-key-ref";
        secretStore.SetSecret(secretRef, "sk-dpapi-stored-secret-123");

        using var configured = await ProviderCredentialFixture.CreateAsync(secretRef, "dpapi-mock", secretStore);
        var service = new ProviderConnectionTestService(secretStore, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);
        var settings = new CustomProviderSettings(
            "dpapi-mock",
            "DPAPI Mock",
            server.BaseUrl,
            apiKeySecretRef: secretRef);

        var result = await service.TestConnectionAsync(settings);

        Assert.True(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.Success, result.Status);
    }

    [Fact]
    public async Task TestConnectionAsync_NeverLeaksApiKeyInErrorMessageOrSanitizedUrl()
    {
        var secretToken = "very-confidential-api-token-xyz987";
        var service = new ProviderConnectionTestService();
        var settings = new CustomProviderSettings(
            "insecure-provider",
            "Insecure Provider",
            "http://remote-external-host.com/v1",
            validateUrl: false);

        var result = await service.TestConnectionAsync(settings, explicitApiKey: secretToken);

        Assert.False(result.IsSuccessful);
        Assert.DoesNotContain(secretToken, result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(secretToken, result.SanitizedEndpointUrl);
    }
}
