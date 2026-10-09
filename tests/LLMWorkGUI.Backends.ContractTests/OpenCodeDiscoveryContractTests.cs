using System.Net;
using System.Text.Json;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Health;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using LLMWorkGUI.Backends.OpenCode.Discovery;
using LLMWorkGUI.Backends.OpenCode.Health;
using LLMWorkGUI.Backends.OpenCode.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeDiscoveryContractTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApiPaths_DeclareDiscoveryEndpoints()
    {
        Assert.Equal("/config/providers", OpenCodeApiPaths.ConfiguredProviders);
        Assert.Equal("/api/provider", OpenCodeApiPaths.ApiProviders);
        Assert.Equal("/provider", OpenCodeApiPaths.Providers);
        Assert.Equal("/api/model", OpenCodeApiPaths.ApiModels);
        Assert.Equal("/model", OpenCodeApiPaths.Models);
        Assert.Equal("/api/provider", OpenCodeApiPaths.ProviderList);
        Assert.Equal("/api/model", OpenCodeApiPaths.ModelList);
    }

    [Fact]
    public void DiscoveryJson_ParsesProviderEnvelopeWithConnectedIds()
    {
        const string Json = """
        {
          "all": [
            {
              "id": "test-provider",
              "name": "Test Provider",
              "baseUrl": "https://example.invalid/v1",
              "models": { "test-model": { "id": "test-model", "name": "Test Model" } }
            },
            { "id": "second-provider", "name": "Second Provider" }
          ],
          "connected": [ "test-provider" ]
        }
        """;

        var providers = OpenCodeDiscoveryJson.ParseProviders(Json);

        Assert.Equal(2, providers.Count);

        var first = providers[0];
        Assert.Equal("test-provider", first.Id);
        Assert.Equal("Test Provider", first.Name);
        Assert.Equal("https://example.invalid/v1", first.BaseUrl);
        Assert.True(first.IsConnected);
        Assert.Equal(new[] { "test-model" }, first.Models);

        var second = providers[1];
        Assert.Equal("second-provider", second.Id);
        Assert.False(second.IsConnected);
        Assert.Empty(second.Models);
    }

    [Fact]
    public void DiscoveryJson_ParsesProviderDictionaryShape()
    {
        const string Json = """
        {
          "test-provider": {
            "name": "Test Provider",
            "models": { "test-model": {}, "second-model": {} }
          }
        }
        """;

        var providers = OpenCodeDiscoveryJson.ParseProviders(Json);

        var provider = Assert.Single(providers);
        Assert.Equal("test-provider", provider.Id);
        Assert.Equal("Test Provider", provider.Name);
        Assert.Equal(new[] { "test-model", "second-model" }, provider.Models);
    }

    [Fact]
    public void DiscoveryJson_ParsesModelVariantsCapabilitiesAndContextLimit()
    {
        const string Json = """
        {
          "models": [
            {
              "id": "test-model",
              "providerID": "test-provider",
              "name": "Test Model",
              "variants": { "low": {}, "max": {} },
              "capabilities": { "reasoning": true, "toolcall": false, "attachment": true },
              "limit": { "context": 200000, "output": 8192 }
            }
          ]
        }
        """;

        var models = OpenCodeDiscoveryJson.ParseModels(Json);

        var model = Assert.Single(models);
        Assert.Equal("test-model", model.Id);
        Assert.Equal("test-provider", model.ProviderId);
        Assert.Equal("Test Model", model.Name);
        Assert.Equal(new[] { "low", "max" }, model.Variants);
        Assert.Equal(new[] { "reasoning", "attachment" }, model.Capabilities);
        Assert.Equal(200000, model.ContextLimit);
    }

    [Fact]
    public void DiscoveryJson_FiltersModelsByProviderId()
    {
        const string Json = """
        [
          { "id": "model-a", "providerID": "provider-a", "name": "Model A" },
          { "id": "model-b", "providerID": "provider-b", "name": "Model B" }
        ]
        """;

        var filtered = OpenCodeDiscoveryJson.ParseModels(Json, "provider-b");

        var model = Assert.Single(filtered);
        Assert.Equal("model-b", model.Id);
    }

    [Fact]
    public void DiscoveryJson_ParsesConfiguredProvidersAndRoundTrips()
    {
        const string Json = """
        {
          "providers": [
            { "id": "test-provider", "name": "Test Provider", "models": [ "test-model" ] }
          ],
          "defaultModels": { "test-provider": "test-model" }
        }
        """;

        var response = OpenCodeDiscoveryJson.ParseConfiguredProviders(Json);

        var provider = Assert.Single(response.Providers);
        Assert.Equal("test-provider", provider.Id);
        Assert.Equal(new[] { "test-model" }, provider.Models);
        Assert.Equal("test-model", response.DefaultModels["test-provider"]);

        var serialized = OpenCodeDiscoveryJson.SerializeConfiguredProviders(response);
        var reparsed = OpenCodeDiscoveryJson.ParseConfiguredProviders(serialized);

        var reparsedProvider = Assert.Single(reparsed.Providers);
        Assert.Equal(provider.Id, reparsedProvider.Id);
        Assert.Equal(provider.Name, reparsedProvider.Name);
        Assert.Equal(new[] { "test-model" }, reparsedProvider.Models);
        Assert.Equal(response.DefaultModels.Count, reparsed.DefaultModels.Count);
        Assert.Equal("test-model", reparsed.DefaultModels["test-provider"]);
    }

    [Theory]
    [InlineData("\"not-a-payload\"")]
    [InlineData("42")]
    public void DiscoveryJson_WhenProviderPayloadRootIsScalar_Throws(string json)
    {
        Assert.Throws<JsonException>(() => OpenCodeDiscoveryJson.ParseProviders(json));
    }

    [Theory]
    [InlineData("\"not-a-payload\"")]
    [InlineData("42")]
    public void DiscoveryJson_WhenModelPayloadRootIsScalar_Throws(string json)
    {
        Assert.Throws<JsonException>(() => OpenCodeDiscoveryJson.ParseModels(json));
    }

    [Fact]
    public void CapabilityCacheOptions_DefaultsToFiveMinutesAndValidates()
    {
        var options = new OpenCodeCapabilityCacheOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), OpenCodeCapabilityCacheOptions.DefaultTtl);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Ttl);

        options.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeCapabilityCacheOptions { Ttl = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeCapabilityCacheOptions { Ttl = TimeSpan.FromMinutes(-1) }.Validate());
    }

    [Fact]
    public async Task CapabilityCache_InvalidationCannotBeUndoneByAnOlderRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<OpenCodeProviderInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubDiscoveryClient(CreateProviders(), CreateModels())
        {
            ProviderHandler = () => { entered.TrySetResult(); return release.Task; }
        };
        using var cache = new OpenCodeCapabilityCacheService(client);
        var read = cache.GetModelsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Invalidate();
        release.TrySetResult(CreateProviders());

        Assert.Empty(await read);
        Assert.False(cache.GetFreshness().IsFresh);
        client.ProviderHandler = null;
        Assert.NotEmpty(await cache.GetModelsAsync());
        Assert.Equal(2, client.ProviderCalls);
    }

    [Fact]
    public async Task CapabilityCache_ReportsFreshnessAndRefreshesExpiredSnapshot()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var client = new StubDiscoveryClient(CreateProviders(), CreateModels());
        using var cache = new OpenCodeCapabilityCacheService(client, timeProvider: timeProvider);

        var initial = cache.GetFreshness();
        Assert.False(initial.IsFresh);
        Assert.Equal(default, initial.LastRefreshedAtUtc);
        Assert.Equal(default, initial.ExpiresAtUtc);

        var models = await cache.GetModelsAsync();

        Assert.Equal("test-model", Assert.Single(models).Id);
        Assert.Equal(1, client.ProviderCalls);
        Assert.Equal(1, client.ModelCalls);

        var fresh = cache.GetFreshness();
        Assert.True(fresh.IsFresh);
        Assert.Equal(TestNow.UtcDateTime, fresh.LastRefreshedAtUtc);
        Assert.Equal(TestNow.UtcDateTime.AddMinutes(5), fresh.ExpiresAtUtc);

        await cache.GetProvidersAsync();
        Assert.Equal(1, client.ProviderCalls);

        timeProvider.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await cache.GetModelsAsync();
        Assert.Equal(1, client.ModelCalls);
        Assert.True(cache.GetFreshness().IsFresh);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await cache.GetModelsAsync();
        Assert.Equal(2, client.ModelCalls);
        Assert.True(cache.GetFreshness().IsFresh);

        await cache.GetModelsAsync(forceRefresh: true);
        Assert.Equal(3, client.ModelCalls);

        cache.Invalidate();

        var invalidated = cache.GetFreshness();
        Assert.False(invalidated.IsFresh);
        Assert.Equal(default, invalidated.LastRefreshedAtUtc);

        await cache.GetProvidersAsync();
        Assert.Equal(4, client.ProviderCalls);
    }

    [Fact]
    public async Task CapabilityCache_GetModelAsyncFindsKnownModelAndReturnsNullForUnknown()
    {
        var client = new StubDiscoveryClient(CreateProviders(), CreateModels());
        using var cache = new OpenCodeCapabilityCacheService(client);

        var known = await cache.GetModelAsync("test-model");
        var unknown = await cache.GetModelAsync("missing-model");

        Assert.NotNull(known);
        Assert.Equal("test-model", known.Id);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task CapabilityCache_WhenDiscoveryFails_RecordsHealthEventAndPropagates()
    {
        var repository = new RecordingHealthStateRepository();
        var collector = new OpenCodeHealthEventCollector(repository, timeProvider: new TestTimeProvider(TestNow));
        var failure = new OpenCodeClientException(
            "The GET /api/provider request returned status code 500.",
            HttpStatusCode.InternalServerError);
        var client = new StubDiscoveryClient(CreateProviders(), CreateModels(), failure);
        using var cache = new OpenCodeCapabilityCacheService(
            client,
            timeProvider: new TestTimeProvider(TestNow),
            healthEventSink: collector);

        await Assert.ThrowsAsync<OpenCodeClientException>(() => cache.GetModelsAsync(forceRefresh: true));

        var record = Assert.Single(repository.Records);
        Assert.Equal(OpenCodeHealthEventCollector.DefaultScopeType, record.ScopeType);
        Assert.Equal(HealthState.Degraded, record.State);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, record.ErrorClass);
        Assert.Equal(1, record.FailureCount);
    }

    [Theory]
    [InlineData(500, HealthErrorClass.Provider4xx5xx)]
    [InlineData(503, HealthErrorClass.Provider4xx5xx)]
    [InlineData(404, HealthErrorClass.Provider4xx5xx)]
    [InlineData(401, HealthErrorClass.AuthenticationOrRefresh)]
    [InlineData(403, HealthErrorClass.AuthenticationOrRefresh)]
    public void HealthCollector_ClassifiesHttpStatusFailures(int statusCode, HealthErrorClass expected)
    {
        var exception = new OpenCodeClientException($"status {statusCode}", (HttpStatusCode)statusCode);

        Assert.Equal(expected, OpenCodeHealthEventCollector.ClassifyFailure(exception));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void HealthCollector_ClassifiesARejectedCredentialWithoutAnException(int statusCode)
    {
        // A rejected credential is an auth failure even when only the status is known, so the breaker
        // can block the account immediately instead of treating it as a generic provider error.
        Assert.Equal(
            HealthErrorClass.AuthenticationOrRefresh,
            OpenCodeHealthEventCollector.ClassifyFailure(exception: null, statusCode));
    }

    [Fact]
    public void HealthCollector_ClassifiesTransportAndProtocolFailures()
    {
        Assert.Equal(
            HealthErrorClass.NetworkOrTimeout,
            OpenCodeHealthEventCollector.ClassifyFailure(new HttpRequestException("connection refused")));
        Assert.Equal(
            HealthErrorClass.NetworkOrTimeout,
            OpenCodeHealthEventCollector.ClassifyFailure(
                new OpenCodeClientException("timed out", new TaskCanceledException())));
        Assert.Equal(
            HealthErrorClass.MalformedProtocolEvent,
            OpenCodeHealthEventCollector.ClassifyFailure(new JsonException("malformed payload")));
        Assert.Equal(
            HealthErrorClass.UnknownOrAmbiguousCompletion,
            OpenCodeHealthEventCollector.ClassifyFailure(new InvalidOperationException("unknown")));
    }

    [Fact]
    public async Task HealthCollector_PersistsDegradedRecordsAndIncrementsFailureCount()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var repository = new RecordingHealthStateRepository();
        var collector = new OpenCodeHealthEventCollector(repository, timeProvider: timeProvider);

        await collector.RecordHealthEventAsync(
            "opencode:test",
            "connection refused",
            new HttpRequestException("connection refused"));

        var first = Assert.Single(repository.Records);
        Assert.Equal($"{OpenCodeHealthEventCollector.DefaultScopeType}:opencode:test", first.Id);
        Assert.Equal(OpenCodeHealthEventCollector.DefaultScopeType, first.ScopeType);
        Assert.Equal("opencode:test", first.ScopeId);
        Assert.Equal(HealthState.Degraded, first.State);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, first.ErrorClass);
        Assert.Equal(1, first.FailureCount);
        Assert.Equal(TestNow, first.WindowStartedAt);
        Assert.Equal(TestNow, first.UpdatedAt);
        Assert.Null(first.CooldownUntil);
        var firstEvidence = first.EvidenceRedactedJson ?? string.Empty;
        Assert.Contains("failureReason", firstEvidence, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", firstEvidence, StringComparison.Ordinal);

        timeProvider.Advance(TimeSpan.FromMinutes(1));

        await collector.RecordExceptionAsync("opencode:test", new JsonException("malformed"));

        var second = Assert.Single(repository.Records);
        Assert.Equal(2, second.FailureCount);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, second.ErrorClass);
        Assert.Equal(TestNow, second.WindowStartedAt);
        Assert.Equal(TestNow.AddMinutes(1), second.UpdatedAt);
    }

    [Fact]
    public void HealthCollector_WhenRepositoryIsMissing_StillAcceptsEvents()
    {
        var collector = new OpenCodeHealthEventCollector();

        var recordTask = collector.RecordHealthEventAsync("opencode:test", "no repository");

        Assert.True(recordTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_LetsARepeatedFailureTripTheBreaker()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var states = new RecordingHealthStateRepository();
        var events = new RecordingHealthEventRepository();
        var healthCenter = CreateHealthCenter(
            states,
            events,
            timeProvider,
            new HealthPolicy { FailureThreshold = 2 });
        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter);
        var scope = OpenCodeHealthEventCollector.ScopeFor("opencode:test");

        await collector.RecordHttpFailureAsync("opencode:test", 500);

        // Below the threshold the backend is degraded but still usable.
        var degraded = await healthCenter.GetSnapshotAsync(scope);
        Assert.Equal(HealthState.Degraded, degraded.State);
        Assert.True(degraded.IsRoutable);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await collector.RecordHttpFailureAsync("opencode:test", 503);

        // The direct-repository path could only ever pin Degraded; through the service the breaker opens.
        var tripped = await healthCenter.GetSnapshotAsync(scope);
        Assert.Equal(HealthState.CoolingDown, tripped.State);
        Assert.False(tripped.IsRoutable);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, tripped.ErrorClass);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_AppendsTheFailureToTheRecoveryAudit()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });
        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter);

        await collector.RecordExceptionAsync("opencode:event-stream", new JsonException("malformed SSE"));

        var audit = await healthCenter.GetAuditAsync(
            OpenCodeHealthEventCollector.ScopeFor("opencode:event-stream"));
        var entry = Assert.Single(audit);

        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, entry.ErrorClass);
        Assert.Equal(HealthState.Degraded, entry.NewState);

        // The very first observation has no previous state; claiming Healthy would invent a history.
        Assert.Null(entry.PreviousState);
        Assert.False(entry.IsVerifiedRecovery);
    }

    [Theory]
    [InlineData("acc-opencode")]
    public async Task HealthCollector_WithAHealthCenter_BlocksTheAccountOnAuthenticationFailure(
        string entityId)
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });
        var accountScope = HealthScope.ForAccount("acc-opencode");
        var routeScope = HealthScope.ForModelRoute("acc-opencode", "test-model");

        await healthCenter.ReportSuccessAsync(accountScope);
        await healthCenter.ReportSuccessAsync(routeScope);

        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter);

        await collector.RecordHttpFailureAsync(entityId, 401);

        // The live 401 is an auth failure, so the account referenced by the observed entity must be
        // blocked immediately even though the transport failure is tracked under the backend scope.
        var account = await healthCenter.GetSnapshotAsync(accountScope);
        Assert.False(account.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, account.ErrorClass);

        // The account block cascades to the model routes the router and the send gate consult.
        var route = await healthCenter.GetSnapshotAsync(routeScope);
        Assert.False(route.IsRoutable);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_BlocksANewAccountWhenOnlyTheServerUrlWasObserved()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });
        var account = new Account(
            "acc-opencode",
            "prov-opencode",
            "OpenCode Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null);
        var accountRepository = new StubAccountRepository(account);
        var profileRepository = new StubProviderProfileRepository(
            new ProviderProfile(
                "prov-opencode",
                "OpenCode Provider",
                BackendType.OpenCode,
                "http://127.0.0.1:5000",
                null,
                DataClassification.PrivateSource,
                true));

        // The account deliberately has no Health Center record yet: the production caller reports the
        // bare server base URL, which carries no account id, so only the repositories can connect the
        // live 401 to the account.
        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter,
            accountRepository: accountRepository,
            profileRepository: profileRepository);

        await collector.RecordHttpFailureAsync("http://127.0.0.1:5000", 401);

        var accountScope = HealthScope.ForAccount("acc-opencode");
        var snapshot = await healthCenter.GetSnapshotAsync(accountScope);

        Assert.False(snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, snapshot.ErrorClass);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_BlocksNewAccountsWhenManagedServeUrlFailsAuthenticationEvenWithDistinctUpstreamProfileUrl()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });
        var account = new Account(
            "acc-upstream",
            "prov-upstream",
            "Upstream Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null);
        var accountRepository = new StubAccountRepository(account);
        var profileRepository = new StubProviderProfileRepository(
            new ProviderProfile(
                "prov-upstream",
                "Upstream Provider",
                BackendType.OpenCode,
                "http://127.0.0.1:11434/v1",
                null,
                DataClassification.PrivateSource,
                true));

        // The production reporter sends the managed serve URL while the profile keeps the distinct
        // upstream endpoint, and the account has no prior health record: only the managed-server
        // detection can connect the live 401 to the account.
        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter,
            accountRepository: accountRepository,
            profileRepository: profileRepository,
            serverOptions: Microsoft.Extensions.Options.Options.Create(new OpenCodeServerOptions
            {
                Hostname = "127.0.0.1",
                Port = 5000
            }));

        await collector.RecordHttpFailureAsync("http://127.0.0.1:5000", 401);

        var snapshot = await healthCenter.GetSnapshotAsync(HealthScope.ForAccount("acc-upstream"));

        Assert.False(snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, snapshot.ErrorClass);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_DoesNotBlockTheAccountForANonAuthenticationFailure()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });
        var accountScope = HealthScope.ForAccount("acc-opencode");

        await healthCenter.ReportSuccessAsync(accountScope);

        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter);

        await collector.RecordHttpFailureAsync("acc-opencode", 500);

        // A generic provider error is not an account-wide block, so the account stays routable.
        var account = await healthCenter.GetSnapshotAsync(accountScope);
        Assert.True(account.IsRoutable);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_DoesNotMoveTheBreakerForAUserCancellation()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 1 });
        var collector = new OpenCodeHealthEventCollector(
            repository: null,
            timeProvider: timeProvider,
            healthCenter: healthCenter);
        var scope = OpenCodeHealthEventCollector.ScopeFor("opencode:test");

        // A user cancellation is classified as NetworkOrTimeout by the transport taxonomy, so this is
        // reported explicitly as an excluded class instead of relying on the exception shape.
        await healthCenter.ReportFailureAsync(scope, HealthErrorClass.UserCancellation);

        var snapshot = await healthCenter.GetSnapshotAsync(scope);

        Assert.Equal(HealthState.Healthy, snapshot.State);
        Assert.True(snapshot.IsRoutable);
        Assert.Equal(0, snapshot.AccountedFailureCount);
    }

    [Fact]
    public async Task HealthCollector_WithAHealthCenter_NeverWritesHealthStatesDirectly()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var bypassRepository = new RecordingHealthStateRepository();
        var healthCenter = CreateHealthCenter(
            new RecordingHealthStateRepository(),
            new RecordingHealthEventRepository(),
            timeProvider,
            new HealthPolicy { FailureThreshold = 3 });

        // Both collaborators are supplied. The service must win, otherwise the state machine and the
        // audit would be bypassed exactly as they were before the migration.
        var collector = new OpenCodeHealthEventCollector(
            bypassRepository,
            timeProvider: timeProvider,
            healthCenter: healthCenter);

        await collector.RecordHttpFailureAsync("opencode:test", 500);

        Assert.Empty(bypassRepository.Records);
    }

    [Fact]
    public void RouteVerification_WhenBindingsMatch_ReturnsMatchedEvidence()
    {
        var timeProvider = new TestTimeProvider(TestNow);
        var service = new OpenCodeRouteVerificationService(timeProvider);
        var requested = CreateBinding();
        var observed = CreateBinding();

        var evidence = service.VerifyRoute(requested, observed, "backend:session-export");

        Assert.True(evidence.IsMatched);
        Assert.Equal(RouteVerificationResult.Matched, evidence.Result);
        Assert.Same(requested, evidence.RequestedBinding);
        Assert.Same(observed, evidence.ObservedBinding);
        Assert.Equal("backend:session-export", evidence.EvidenceSource);
        Assert.Equal(TestNow.UtcDateTime, evidence.CheckedAtUtc);
        Assert.Empty(evidence.Mismatches);

        service.EnsureRouteMatches(requested, observed);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("providerProfile")]
    [InlineData("account")]
    [InlineData("model")]
    [InlineData("reasoning")]
    [InlineData("speed")]
    [InlineData("mode")]
    public void RouteVerification_WhenSingleBindingFieldDiffers_DetectsMismatch(string field)
    {
        var service = new OpenCodeRouteVerificationService();
        var requested = CreateBinding();
        var observed = field switch
        {
            "backend" => CreateBinding(backend: BackendType.CursorAcp),
            "providerProfile" => CreateBinding(providerProfileId: "profile-2"),
            "account" => CreateBinding(accountId: "other@example.com"),
            "model" => CreateBinding(modelId: "other-model"),
            "reasoning" => CreateBinding(reasoningEffort: "low"),
            "speed" => CreateBinding(speedMode: "slow"),
            "mode" => CreateBinding(executionMode: "plan"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown binding field.")
        };

        var evidence = service.VerifyRoute(requested, observed);

        Assert.False(evidence.IsMatched);
        Assert.Equal(RouteVerificationResult.Mismatch, evidence.Result);
        Assert.Contains(evidence.Mismatches, mismatch => mismatch.StartsWith($"{field}:", StringComparison.Ordinal));

        var exception = Assert.Throws<RouteMismatchException>(
            () => service.EnsureRouteMatches(requested, observed));

        Assert.Same(evidence.RequestedBinding, exception.RouteEvidence.RequestedBinding);
        Assert.False(exception.RouteEvidence.IsMatched);
    }

    [Fact]
    public void RouteVerification_WhenObservedRouteIsMissing_ReturnsOpaqueEvidenceAndThrows()
    {
        var service = new OpenCodeRouteVerificationService();
        var requested = CreateBinding();

        var evidence = service.VerifyRoute(requested, observed: null, "plugin:bridge");

        Assert.False(evidence.IsMatched);
        Assert.Equal(RouteVerificationResult.OpaqueRouteMissingEvidence, evidence.Result);
        Assert.Null(evidence.ObservedBinding);
        Assert.Same(requested, evidence.RequestedBinding);
        Assert.NotEmpty(evidence.Mismatches);

        var exception = Assert.Throws<RouteMismatchException>(
            () => service.EnsureRouteMatches(requested, observed: null));

        Assert.Equal(RouteVerificationResult.OpaqueRouteMissingEvidence, exception.RouteEvidence.Result);
        Assert.False(OpenCodeRouteVerificationService.TreatRequestedAsObservedAllowed);
    }

    [Fact]
    public void AddOpenCodeBackend_RegistersDiscoveryRoutingAndHealthServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IOpenCodeCapabilityCacheService>();
        var routing = provider.GetRequiredService<IRouteVerificationService>();
        var health = provider.GetRequiredService<IOpenCodeHealthEventSink>();

        Assert.IsType<OpenCodeCapabilityCacheService>(cache);
        Assert.IsType<OpenCodeRouteVerificationService>(routing);
        Assert.IsType<OpenCodeHealthEventCollector>(health);
        Assert.Same(cache, provider.GetRequiredService<IOpenCodeCapabilityCacheService>());
        Assert.Same(routing, provider.GetRequiredService<IRouteVerificationService>());
        Assert.Same(health, provider.GetRequiredService<IOpenCodeHealthEventSink>());
    }

    [Fact]
    public void AddOpenCodeBackend_WhenCapabilityCacheTtlIsInvalid_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.Configure<OpenCodeCapabilityCacheOptions>(options => options.Ttl = TimeSpan.Zero);
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OpenCodeCapabilityCacheOptions>>().Value);
    }

    private static SessionBinding CreateBinding(
        BackendType backend = BackendType.OpenCode,
        string providerProfileId = "profile-1",
        string accountId = "user@example.com",
        string modelId = "test-model",
        string? reasoningEffort = "high",
        string? speedMode = "fast",
        string? executionMode = "build")
    {
        return new SessionBinding(
            backend,
            providerProfileId,
            accountId,
            modelId,
            reasoningEffort,
            speedMode,
            executionMode);
    }

    private static IReadOnlyList<OpenCodeProviderInfo> CreateProviders()
    {
        return new[]
        {
            new OpenCodeProviderInfo
            {
                Id = "test-provider",
                Name = "Test Provider",
                IsConnected = true,
                Models = new[] { "test-model" }
            }
        };
    }

    private static IReadOnlyList<OpenCodeModelInfo> CreateModels()
    {
        return new[]
        {
            new OpenCodeModelInfo
            {
                Id = "test-model",
                ProviderId = "test-provider",
                Name = "Test Model",
                Variants = new[] { "max" },
                Capabilities = new[] { "reasoning" },
                ContextLimit = 200000
            }
        };
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

    private static HealthCenterService CreateHealthCenter(IHealthStateRepository states, IHealthEventRepository events,
        TimeProvider time, HealthPolicy policy) => new(states, events, time, policy,
            transitionStore: new RecordingTransitionStore(states, events));

    // In-memory contract double; SQLite transaction/failure atomicity is exercised in IntegrationTests.
    private sealed class RecordingTransitionStore(IHealthStateRepository states, IHealthEventRepository events)
        : IHealthTransitionStore, IHealthAuthenticationFanoutStore
    {
        private readonly List<HealthAuthenticationProjection> _pending = [];
        public async Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent, CancellationToken token = default)
        {
            if (state is not null) await states.UpsertAsync(state, token);
            if (healthEvent is not null) await events.AppendAsync(healthEvent, token);
        }
        public Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> items, CancellationToken token)
        { _pending.AddRange(items); return Task.CompletedTask; }
        public Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<HealthAuthenticationProjection>>(_pending.ToArray());
        public Task<bool> HasPendingAsync(HealthScope scope, CancellationToken token)
            => Task.FromResult(_pending.Any(p => p.Scope == scope ||
                (scope.ScopeType == HealthScope.AccountScopeType && p.AccountId == scope.ScopeId)));
        public async Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken token)
            => (await states.GetAsync(scope.ScopeType, scope.ScopeId, token), await HasPendingAsync(scope, token));
        public async Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent, string id, CancellationToken token)
        { await SaveAsync(state, healthEvent, token); _pending.RemoveAll(p => p.Id == id); }
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

    /// <summary>
    /// Append-only audit double. It refuses a duplicate id, mirroring the SQLite store, and orders
    /// same-timestamp entries by insertion so the audit cannot come back scrambled.
    /// </summary>
    private sealed class RecordingHealthEventRepository : IHealthEventRepository
    {
        private readonly List<HealthEventRecord> _events = new();

        public Task AppendAsync(HealthEventRecord healthEvent, CancellationToken cancellationToken = default)
        {
            if (_events.Any(existing => existing.Id == healthEvent.Id))
            {
                throw new InvalidOperationException(
                    $"The health event '{healthEvent.Id}' was already appended; the audit is append-only.");
            }

            _events.Add(healthEvent);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(
            string scopeType,
            string scopeId,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var matches = _events
                .Select((record, index) => (Record: record, Index: index))
                .Where(entry => entry.Record.ScopeType == scopeType && entry.Record.ScopeId == scopeId)
                .OrderByDescending(entry => entry.Record.OccurredAt)
                .ThenByDescending(entry => entry.Index)
                .Select(entry => entry.Record);

            if (limit is not null)
            {
                matches = matches.Take(limit.Value);
            }

            return Task.FromResult<IReadOnlyList<HealthEventRecord>>(matches.ToArray());
        }

        public Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            var recent = _events
                .Select((record, index) => (Record: record, Index: index))
                .OrderByDescending(entry => entry.Record.OccurredAt)
                .ThenByDescending(entry => entry.Index)
                .Take(limit)
                .Select(entry => entry.Record)
                .ToArray();

            return Task.FromResult<IReadOnlyList<HealthEventRecord>>(recent);
        }
    }

    private sealed class StubAccountRepository : IAccountRepository
    {
        private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

        public StubAccountRepository(params Account[] accounts)
        {
            foreach (var account in accounts)
            {
                _accounts[account.Id] = account;
            }
        }

        public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);
        }

        public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(
            string providerProfileId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Account>>(
                _accounts.Values
                    .Where(account => string.Equals(
                        account.ProviderProfileId,
                        providerProfileId,
                        StringComparison.Ordinal))
                    .ToArray());
        }

        public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(Account account, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateAuthStateAsync(
            string id,
            AuthState authState,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateCooldownAsync(
            string id,
            DateTimeOffset? cooldownUntil,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string?> GetSecretReferenceAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubProviderProfileRepository : IProviderProfileRepository
    {
        private readonly List<ProviderProfile> _profiles;

        public StubProviderProfileRepository(params ProviderProfile[] profiles)
        {
            _profiles = profiles.ToList();
        }

        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.ToArray());
        }

        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)));
        }

        public Task UpsertAsync(
            ProviderProfile profile,
            string? apiKeySecretReference = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string?> GetApiKeySecretReferenceAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubDiscoveryClient : IOpenCodeClient
    {
        private readonly Exception? _failure;
        public Func<Task<IReadOnlyList<OpenCodeProviderInfo>>>? ProviderHandler { get; set; }

        public StubDiscoveryClient(
            IReadOnlyList<OpenCodeProviderInfo> providers,
            IReadOnlyList<OpenCodeModelInfo> models,
            Exception? failure = null)
        {
            Providers = providers;
            Models = models;
            _failure = failure;
        }

        public IReadOnlyList<OpenCodeProviderInfo> Providers { get; }

        public IReadOnlyList<OpenCodeModelInfo> Models { get; }

        public int ProviderCalls { get; private set; }

        public int ModelCalls { get; private set; }

        public Uri BaseUrl { get; } = new("http://127.0.0.1:54321");

        public Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            ProviderCalls++;

            if (ProviderHandler is not null) return ProviderHandler();

            return _failure is null
                ? Task.FromResult(Providers)
                : Task.FromException<IReadOnlyList<OpenCodeProviderInfo>>(_failure);
        }

        public Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
            string? providerId = null,
            CancellationToken cancellationToken = default)
        {
            ModelCalls++;

            if (_failure is not null)
            {
                return Task.FromException<IReadOnlyList<OpenCodeModelInfo>>(_failure);
            }

            if (providerId is null)
            {
                return Task.FromResult(Models);
            }

            return Task.FromResult<IReadOnlyList<OpenCodeModelInfo>>(
                Models.Where(model => string.Equals(model.ProviderId, providerId, StringComparison.Ordinal))
                    .ToArray());
        }

        public Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse> CreateSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse> ForkSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> AbortSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> SendPromptAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ReplyPermissionAsync(
            string permissionId,
            OpenCodePermissionReply reply,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
