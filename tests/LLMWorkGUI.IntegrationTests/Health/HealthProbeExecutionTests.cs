using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

/// <summary>
/// End-to-end probe execution against a real loopback HTTP server and real SQLite stores. The probe
/// result is observed from an actual network call, not supplied by the test, and no model quota is
/// spent because the endpoint is a local OpenAI-compatible mock (ROADMAP Phase 7).
/// </summary>
public sealed partial class HealthProbeExecutionTests
{
    private const string AccountId = "acc-probe-live";
    private const string ProviderProfileId = "prov-probe-live";

    private static readonly HealthScope AccountScope = HealthScope.ForAccount(AccountId);

    [Fact]
    public async Task AConnectionProbeAgainstAHealthyLoopbackProvider_ConfirmsConnectionOnly()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.Equal(HealthProbeKind.Connection, outcome.Kind);

        // The endpoint really was reached, so the latency is an observed measurement.
        Assert.NotNull(outcome.ProbedEndpoint);
        Assert.Contains("/models", outcome.ProbedEndpoint!, StringComparison.Ordinal);

        // A bare GET /models never verifies a recovery: the scope stays out of routing and the audit
        // records the observation without calling it a verified recovery (ТЗ §6.10).
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Recovering, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        var audit = await fixture.HealthCenter.GetAuditAsync(AccountScope);
        Assert.False(audit[0].IsVerifiedRecovery);
        Assert.Equal(HealthState.Recovering, audit[0].NewState);
    }

    [Fact]
    public async Task AModelProbeAgainstAHealthyLoopbackProvider_ProducesAVerifiedRecovery()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.Equal(HealthProbeKind.Model, outcome.Kind);

        // Only the pinned model probe confirms auth, the selected model and a minimal turn.
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);
        Assert.NotNull(outcome.ProbedEndpoint);
        Assert.Contains("/chat/completions", outcome.ProbedEndpoint!, StringComparison.Ordinal);

        // The recovery is persisted as verified, not forced.
        var audit = await fixture.HealthCenter.GetAuditAsync(AccountScope);
        Assert.True(audit[0].IsVerifiedRecovery);
        Assert.False(audit[0].IsForcedRecovery);
    }

    [Fact]
    public async Task AModelProbe_WhenProviderReturnsMismatchedModel_BlocksOnlyTheModelRoute()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionModelOverride = "wrong-model";
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.ModelUnavailableOrMismatch, outcome.ErrorClass);

        // ТЗ §6.10: a model mismatch blocks only the concrete model route, so the account must not be
        // quarantined. It still owes a passing probe and therefore stays out of routing.
        Assert.Equal(HealthState.Recovering, outcome.Snapshot.State);
        Assert.NotEqual(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        var routeScope = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o");
        var routeSnapshot = await fixture.HealthCenter.GetSnapshotAsync(routeScope);
        Assert.False(routeSnapshot.IsRoutable);

        var routeAudit = await fixture.HealthCenter.GetAuditAsync(routeScope);
        Assert.Contains(routeAudit, entry => entry.ErrorClass == HealthErrorClass.ModelUnavailableOrMismatch);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_ResolvesTheTargetThroughTheAccountAndRestoresTheRouteToHealthy()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        var routeScope = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o");

        await fixture.HealthCenter.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        await fixture.HealthCenter.ExpireCooldownAsync(routeScope);

        var outcome = await fixture.Probe.ProbeModelAsync(
            routeScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);

        // The route scope carries no endpoint of its own, so the probe could only run because the
        // account and its provider profile were resolved from the route id.
        Assert.NotNull(outcome.ProbedEndpoint);
        Assert.Contains("/chat/completions", outcome.ProbedEndpoint!, StringComparison.Ordinal);

        var routeSnapshot = await fixture.HealthCenter.GetSnapshotAsync(routeScope);
        Assert.Equal(HealthState.Healthy, routeSnapshot.State);
        Assert.True(routeSnapshot.IsRoutable);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_WhenConfirmationModelDiffers_IsRefusedWithoutExecutingOrRestoring()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        var routeScope = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o");

        await fixture.HealthCenter.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        await fixture.HealthCenter.ExpireCooldownAsync(routeScope);

        var outcome = await fixture.Probe.ProbeModelAsync(
            routeScope,
            HealthProbeConfirmation.ForModel("mock-claude-3-5-sonnet", costPreviewAcknowledged: true));

        Assert.False(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthProbeRefusal.ProbeModelUnsupported, outcome.Refusal);
        Assert.Equal(0, server.RequestCount);

        var routeSnapshot = await fixture.HealthCenter.GetSnapshotAsync(routeScope);
        Assert.NotEqual(HealthState.Healthy, routeSnapshot.State);
        Assert.False(routeSnapshot.IsRoutable);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_WhenReceiving401_BlocksAccountAndAllItsRoutes()
    {
        await using var server = new LocalMockAiServer();
        server.SimulatedStatusCode = 401;
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        var route1 = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o");
        var route2 = HealthScope.ForModelRoute(AccountId, "mock-claude-3-5-sonnet");
        var route3 = HealthScope.ForModelRoute(AccountId, "mock-other");

        // Seed route1 into ProbeRequired
        await fixture.HealthCenter.ReportFailureAsync(route1, HealthErrorClass.NetworkOrTimeout);
        await fixture.HealthCenter.ExpireCooldownAsync(route1);

        // Seed route2 and route3 into Healthy
        await fixture.HealthCenter.ReportSuccessAsync(route2);
        await fixture.HealthCenter.ReportSuccessAsync(route3);

        var outcome = await fixture.Probe.ProbeModelAsync(
            route1,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, outcome.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        // The account itself must be blocked (ТЗ §6.10)
        var accountSnapshot = await fixture.HealthCenter.GetSnapshotAsync(AccountScope);
        Assert.False(accountSnapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, accountSnapshot.ErrorClass);

        // All other routes of the account must also be blocked (ТЗ §6.10)
        var route2Snapshot = await fixture.HealthCenter.GetSnapshotAsync(route2);
        Assert.False(route2Snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, route2Snapshot.ErrorClass);

        var route3Snapshot = await fixture.HealthCenter.GetSnapshotAsync(route3);
        Assert.False(route3Snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, route3Snapshot.ErrorClass);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", false)]
    [InlineData("gpt-4o-2024-08-06", false)]
    [InlineData("openai/gpt-4o", false)]
    [InlineData("GPT-4O", false)]
    [InlineData("gpt-4o ", false)]
    [InlineData(" gpt-4o", false)]
    public async Task AModelProbe_ConfirmsOnlyTheExactRequestedModelWithoutInventingAliasEvidence(
        string returnedModel,
        bool expectedToMatch)
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-model-match",
            @object = "chat.completion",
            model = returnedModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = "ok" },
                    finish_reason = "stop"
                }
            }
        });
        await server.StartAsync();

        var executor = new ProviderModelProbeExecutor();

        var result = await executor.ExecuteAsync(new ModelProbeRequest
        {
            Scope = HealthScope.ForModelRoute(AccountId, "gpt-4o"),
            ProviderProfileId = ProviderProfileId,
            BaseUrl = server.BaseUrl,
            ModelId = "gpt-4o"
        });

        if (expectedToMatch)
        {
            Assert.Equal(ModelProbeOutcome.Succeeded, result.Outcome);
        }
        else
        {
            // A generic proxy's alias/snapshot/namespace is not proof of this exact scope's model.
            Assert.Equal(ModelProbeOutcome.Failed, result.Outcome);
            Assert.Equal(HealthErrorClass.ModelUnavailableOrMismatch, result.FailureClass);
        }
    }

    [Fact]
    public async Task AModelProbe_WhenTheAssistantMessageHasEmptyContent_FailsWithMalformedProtocol()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-empty-content",
            @object = "chat.completion",
            model = "mock-gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = string.Empty },
                    finish_reason = "stop"
                }
            }
        });
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);
        await MoveToProbeRequiredAsync(fixture);
        var result = await fixture.Probe.ProbeModelAsync(AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        // A bare assistant role is not a completed minimal turn: it must carry content, tool calls or
        // text before the pinned probe may claim a verified recovery (ТЗ §6.10).
        Assert.True(result.WasExecuted);
        Assert.False(result.Succeeded);
        Assert.False(result.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, result.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbe_WhenTheAssistantMessageHasNoContentProperty_FailsWithMalformedProtocol()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride =
            "{ \"id\": \"chatcmpl-no-content\", \"model\": \"mock-gpt-4o\", \"choices\": [ { \"index\": 0, " +
            "\"message\": { \"role\": \"assistant\" }, \"finish_reason\": \"stop\" } ] }";
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);
        await MoveToProbeRequiredAsync(fixture);
        var result = await fixture.Probe.ProbeModelAsync(AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(result.WasExecuted);
        Assert.False(result.Succeeded);
        Assert.False(result.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, result.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbe_WhenTheMessageHasUserRoleWithContent_FailsAsNotCompletedAssistantTurn()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-user-role",
            @object = "chat.completion",
            model = "mock-gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "user", content = "Hello I am a user" },
                    finish_reason = "stop"
                }
            }
        });
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);
        await MoveToProbeRequiredAsync(fixture);
        var result = await fixture.Probe.ProbeModelAsync(AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        // A non-assistant role message is not a completed minimal turn (ТЗ §6.10).
        Assert.True(result.WasExecuted);
        Assert.False(result.Succeeded);
        Assert.False(result.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, result.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbe_WhenTheAssistantMessageCarriesToolCalls_CompletesTheMinimalTurn()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-tool-call",
            @object = "chat.completion",
            model = "mock-gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call-1",
                                type = "function",
                                function = new { name = "probe", arguments = "{}" }
                            }
                        }
                    },
                    finish_reason = "tool_calls"
                }
            }
        });
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);
        await MoveToProbeRequiredAsync(fixture);
        var result = await fixture.Probe.ProbeModelAsync(AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(result.WasExecuted);
        Assert.True(result.Succeeded);
        Assert.True(result.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, result.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbe_WhenProviderReturnsEmptyChoices_FailsWithMalformedProtocol()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-empty-choices",
            @object = "chat.completion",
            model = "mock-gpt-4o",
            choices = Array.Empty<object>()
        });
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, outcome.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbe_WhenProviderReturnsMalformedJson_FailsWithMalformedProtocol()
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = "{ \"model\": \"mock-gpt-4o\", \"choices\": [";
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel("mock-gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, outcome.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AProbeAgainstAFailingLoopbackProvider_QuarantinesWithTheObservedClass()
    {
        await using var server = new LocalMockAiServer();
        server.SimulatedStatusCode = 503;
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, outcome.ErrorClass);
    }

    [Fact]
    public async Task AProbeRejectedByTheProvider_IsClassifiedAsAnAuthenticationFailure()
    {
        await using var server = new LocalMockAiServer();

        // The provider requires a key. The profile has none, so the call is rejected for real.
        server.ExpectedApiKey = "sk-valid-mock-token";
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);

        var outcome = await fixture.Probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, outcome.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
    }

    [Fact]
    public async Task ProbeEvidence_IsPersistedRedactedInTheRecoveryAudit()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        await MoveToProbeRequiredAsync(fixture);
        await fixture.Probe.ProbeConnectionAsync(AccountScope);

        var stored = await fixture.Events.ListByScopeAsync(AccountScope.ScopeType, AccountScope.ScopeId);
        var withEvidence = stored.First(record => record.EvidenceRedactedJson is not null);

        using var document = JsonDocument.Parse(withEvidence.EvidenceRedactedJson!);
        var root = document.RootElement;

        Assert.Equal("providerConnection", root.GetProperty("probe").GetString());
        Assert.Equal(nameof(ProviderConnectionStatus.Success), root.GetProperty("status").GetString());

        // The mock server publishes at least two models, and the probe observed them.
        Assert.True(root.GetProperty("discoveredModelCount").GetInt32() >= 2);

        // Only a sanitized endpoint is stored: no query string, no credential, no response body.
        var endpoint = root.GetProperty("endpoint").GetString()!;
        Assert.DoesNotContain("?", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", withEvidence.EvidenceRedactedJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProbeIsRefused_WhenTheScopeIsNotAwaitingOne()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var database = new TestDatabase();
        var fixture = await CreateFixtureAsync(database, server.BaseUrl);

        // The scope is Healthy, so nothing owes a probe.
        var outcome = await fixture.Probe.ProbeConnectionAsync(AccountScope);

        Assert.False(outcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.NoProbePending, outcome.Refusal);

        // Nothing was written: a refusal is not a transition.
        var stored = await fixture.Events.ListByScopeAsync(AccountScope.ScopeType, AccountScope.ScopeId);
        Assert.Empty(stored);
    }

    private static async Task MoveToProbeRequiredAsync(ProbeFixture fixture)
    {
        await fixture.HealthCenter.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        await fixture.HealthCenter.ExpireCooldownAsync(AccountScope);

        var snapshot = await fixture.HealthCenter.GetSnapshotAsync(AccountScope);

        Assert.Equal(HealthState.ProbeRequired, snapshot.State);
    }

    private static async Task<ProbeFixture> CreateFixtureAsync(TestDatabase database, string baseUrl)
    {
        await database.InitializeAsync();

        var states = new SqliteHealthStateRepository(database.Factory);
        var events = new SqliteHealthEventRepository(database.Factory);
        var accounts = new SqliteAccountRepository(database.Factory);
        var profiles = new SqliteProviderProfileRepository(database.Factory);

        await profiles.UpsertAsync(new ProviderProfile(
            ProviderProfileId,
            "Loopback Mock Provider",
            BackendType.OpenCode,
            baseUrl,
            null,
            DataClassification.PublicSource,
            true));

        await accounts.SaveAsync(new Account(
            AccountId,
            ProviderProfileId,
            "Loopback Probe Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null));

        // The cooldown is expired immediately, so the policy uses a zero-length cooldown instead of the
        // test waiting on a real clock.
        var policy = new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromTicks(1)
        };

        var healthCenter = new HealthCenterService(states, events, TimeProvider.System, policy);
        var connectionTest = new ProviderConnectionTestService();

        // Both probes are wired so the same fixture proves the connection-only outcome and the pinned
        // model probe that is the only path to a verified recovery (ТЗ §6.10).
        var probe = new HealthProbeService(
            healthCenter,
            profiles,
            accounts,
            connectionTest,
            new ProviderModelProbeExecutor());

        return new ProbeFixture(healthCenter, probe, events);
    }

    private sealed record ProbeFixture(
        HealthCenterService HealthCenter,
        HealthProbeService Probe,
        SqliteHealthEventRepository Events);
}
