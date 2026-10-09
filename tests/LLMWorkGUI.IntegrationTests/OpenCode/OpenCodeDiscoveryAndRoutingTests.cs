using System.Net;
using System.Text.Json;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Discovery;
using LLMWorkGUI.Backends.OpenCode.Health;
using LLMWorkGUI.Backends.OpenCode.Routing;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeDiscoveryAndRoutingTests
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:54321");

    private static readonly DateTimeOffset TestNow = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const string ProvidersJson = """
    {
      "all": [
        {
          "id": "test-provider",
          "name": "Test Provider",
          "baseUrl": "https://example.invalid/v1",
          "models": { "test-model": {} }
        }
      ],
      "connected": [ "test-provider" ]
    }
    """;

    private const string ModelsJson = """
    {
      "models": [
        {
          "id": "test-model",
          "providerID": "test-provider",
          "name": "Test Model",
          "variants": { "max": {} },
          "capabilities": { "reasoning": true },
          "limit": { "context": 200000 }
        },
        { "id": "second-model", "providerID": "second-provider", "name": "Second Model" }
      ]
    }
    """;

    private const string ConfiguredProvidersJson = """
    {
      "providers": [
        { "id": "test-provider", "name": "Test Provider", "models": [ "test-model" ] }
      ],
      "defaultModels": { "test-provider": "test-model" }
    }
    """;

    [Fact]
    public async Task ListProvidersAsync_UsesApiProviderEndpointAndParsesPayload()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json(ProvidersJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var providers = await client.ListProvidersAsync();

        var provider = Assert.Single(providers);
        Assert.Equal("test-provider", provider.Id);
        Assert.Equal("Test Provider", provider.Name);
        Assert.Equal("https://example.invalid/v1", provider.BaseUrl);
        Assert.True(provider.IsConnected);
        Assert.Equal(new[] { "test-model" }, provider.Models);
        Assert.Equal(("GET", "/api/provider"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task ListProvidersAsync_WhenApiEndpointIsMissing_FallsBackToLegacyPath()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath == "/api/provider"
                ? StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
                : StubHttpMessageHandler.Json(ProvidersJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var providers = await client.ListProvidersAsync();

        Assert.Single(providers);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/provider", handler.Requests[0].Path);
        Assert.Equal("/provider", handler.Requests[1].Path);
    }

    [Fact]
    public async Task ListModelsAsync_UsesApiModelEndpointAndFiltersByProvider()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json(ModelsJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var models = await client.ListModelsAsync("second-provider");

        var model = Assert.Single(models);
        Assert.Equal("second-model", model.Id);
        Assert.Equal("second-provider", model.ProviderId);
        Assert.Equal(("GET", "/api/model"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task ListModelsAsync_WhenApiEndpointIsMissing_FallsBackToLegacyPath()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath == "/api/model"
                ? StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
                : StubHttpMessageHandler.Json(ModelsJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var models = await client.ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/model", handler.Requests[0].Path);
        Assert.Equal("/model", handler.Requests[1].Path);
    }

    [Fact]
    public async Task ListConfiguredProvidersAsync_ParsesProvidersAndDefaultModels()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json(ConfiguredProvidersJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var response = await client.ListConfiguredProvidersAsync();

        var provider = Assert.Single(response.Providers);
        Assert.Equal("test-provider", provider.Id);
        Assert.Equal(new[] { "test-model" }, provider.Models);
        Assert.Equal("test-model", response.DefaultModels["test-provider"]);
        Assert.Equal(("GET", "/config/providers"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task ListProvidersAsync_WhenServerFails_ThrowsClientExceptionWithStatusCode()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(
                StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(
            () => client.ListProvidersAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task ListModelsAsync_WhenPayloadIsMalformed_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{ not json")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(
            () => client.ListModelsAsync());

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task CapabilityCache_RefreshesOnMissAndServesFreshHits()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(
                request.RequestUri?.AbsolutePath == "/api/model" ? ModelsJson : ProvidersJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        var timeProvider = new TestTimeProvider(TestNow);
        using var cache = new OpenCodeCapabilityCacheService(client, timeProvider: timeProvider);

        Assert.False(cache.GetFreshness().IsFresh);

        var models = await cache.GetModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.True(cache.GetFreshness().IsFresh);
        Assert.Equal(2, handler.Requests.Count);

        await cache.GetModelsAsync();
        Assert.Equal(2, handler.Requests.Count);

        var providers = await cache.GetProvidersAsync();

        Assert.Equal("test-provider", Assert.Single(providers).Id);
        Assert.Equal(2, handler.Requests.Count);

        timeProvider.Advance(TimeSpan.FromMinutes(6));

        await cache.GetModelsAsync();

        Assert.Equal(4, handler.Requests.Count);
        Assert.True(cache.GetFreshness().IsFresh);

        await cache.GetModelsAsync(forceRefresh: true);

        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task CapabilityCache_GetModelAsyncFindsKnownModelAndReturnsNullForUnknown()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(
                request.RequestUri?.AbsolutePath == "/api/model" ? ModelsJson : ProvidersJson)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        using var cache = new OpenCodeCapabilityCacheService(client);

        var known = await cache.GetModelAsync("test-model");
        var unknown = await cache.GetModelAsync("missing-model");

        Assert.NotNull(known);
        Assert.Equal("test-provider", known.ProviderId);
        Assert.Equal(new[] { "max" }, known.Variants);
        Assert.Equal(new[] { "reasoning" }, known.Capabilities);
        Assert.Equal(200000, known.ContextLimit);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task CapabilityCache_WhenServerReturns500_RecordsProviderHealthEvent()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(
                StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        var repository = new RecordingHealthStateRepository();
        var collector = new OpenCodeHealthEventCollector(
            repository,
            timeProvider: new TestTimeProvider(TestNow));
        using var cache = new OpenCodeCapabilityCacheService(
            client,
            timeProvider: new TestTimeProvider(TestNow),
            healthEventSink: collector);

        await Assert.ThrowsAsync<OpenCodeClientException>(
            () => cache.GetModelsAsync(forceRefresh: true));

        var record = Assert.Single(repository.Records);
        Assert.Equal("OpenCodeBackend", record.ScopeType);
        Assert.Equal(HealthState.Degraded, record.State);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, record.ErrorClass);
        Assert.Equal(1, record.FailureCount);
        Assert.Contains("500", record.EvidenceRedactedJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityCache_WhenConnectionFails_RecordsNetworkHealthEvent()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        var repository = new RecordingHealthStateRepository();
        var collector = new OpenCodeHealthEventCollector(
            repository,
            timeProvider: new TestTimeProvider(TestNow));
        using var cache = new OpenCodeCapabilityCacheService(
            client,
            timeProvider: new TestTimeProvider(TestNow),
            healthEventSink: collector);

        await Assert.ThrowsAsync<OpenCodeClientException>(
            () => cache.GetModelsAsync(forceRefresh: true));
        await Assert.ThrowsAsync<OpenCodeClientException>(
            () => cache.GetModelsAsync(forceRefresh: true));

        var record = Assert.Single(repository.Records);
        Assert.Equal(HealthState.Degraded, record.State);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, record.ErrorClass);
        Assert.Equal(2, record.FailureCount);
    }

    [Fact]
    public async Task HealthCollector_WhenStreamPayloadIsMalformed_RecordsMalformedProtocolEvent()
    {
        var repository = new RecordingHealthStateRepository();
        var collector = new OpenCodeHealthEventCollector(
            repository,
            timeProvider: new TestTimeProvider(TestNow));

        await collector.RecordExceptionAsync(
            "opencode:event-stream",
            new JsonException("malformed SSE payload"));

        var record = Assert.Single(repository.Records);
        Assert.Equal("opencode:event-stream", record.ScopeId);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, record.ErrorClass);
        Assert.Equal(1, record.FailureCount);
    }

    [Fact]
    public void RouteVerification_WhenObservedMatches_AllowsExecution()
    {
        var service = new OpenCodeRouteVerificationService(new TestTimeProvider(TestNow));
        var requested = CreateBinding();

        service.EnsureRouteMatches(requested, CreateBinding());

        var evidence = service.VerifyRoute(requested, CreateBinding(), "backend:evidence");
        Assert.True(evidence.IsMatched);
        Assert.Equal(RouteVerificationResult.Matched, evidence.Result);
    }

    [Fact]
    public void RouteVerification_WhenObservedDiffers_ThrowsRouteMismatch()
    {
        var service = new OpenCodeRouteVerificationService(new TestTimeProvider(TestNow));
        var requested = CreateBinding();

        var exception = Assert.Throws<RouteMismatchException>(
            () => service.EnsureRouteMatches(requested, CreateBinding(modelId: "other-model")));

        Assert.False(exception.RouteEvidence.IsMatched);
        Assert.Equal(RouteVerificationResult.Mismatch, exception.RouteEvidence.Result);
        Assert.Contains(
            exception.RouteEvidence.Mismatches,
            mismatch => mismatch.StartsWith("model:", StringComparison.Ordinal));
    }

    [Fact]
    public void RouteVerification_WhenObservedRouteIsMissing_ThrowsRouteMismatch()
    {
        var service = new OpenCodeRouteVerificationService(new TestTimeProvider(TestNow));
        var requested = CreateBinding();

        var exception = Assert.Throws<RouteMismatchException>(
            () => service.EnsureRouteMatches(requested, observed: null, "plugin:bridge"));

        Assert.Equal(RouteVerificationResult.OpaqueRouteMissingEvidence, exception.RouteEvidence.Result);
        Assert.Equal("plugin:bridge", exception.RouteEvidence.EvidenceSource);
        Assert.Null(exception.RouteEvidence.ObservedBinding);
    }

    private static SessionBinding CreateBinding(
        string providerProfileId = "profile-1",
        string accountId = "user@example.com",
        string modelId = "test-model",
        string? reasoningEffort = "high",
        string? speedMode = "fast",
        string? executionMode = "build")
    {
        return new SessionBinding(
            BackendType.OpenCode,
            providerProfileId,
            accountId,
            modelId,
            reasoningEffort,
            speedMode,
            executionMode);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public TestTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }

        public void Advance(TimeSpan delta)
        {
            UtcNow += delta;
        }
    }

    private sealed class RecordingHealthStateRepository : IHealthStateRepository
    {
        private readonly Dictionary<(string ScopeType, string ScopeId), HealthStateRecord> _records = new();

        public IReadOnlyCollection<HealthStateRecord> Records => _records.Values;

        public Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default)
        {
            _records[(healthState.ScopeType, healthState.ScopeId)] = healthState;

            return Task.CompletedTask;
        }

        public Task<HealthStateRecord?> GetAsync(
            string scopeType,
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _records.TryGetValue((scopeType, scopeId), out var record) ? record : null);
        }

        public Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<HealthStateRecord>>(_records.Values.ToArray());
        }
    }
}
