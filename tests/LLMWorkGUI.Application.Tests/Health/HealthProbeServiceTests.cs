using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// Probe execution (ROADMAP Phase 7 "connection/model probes"). The caller never supplies the outcome:
/// the service calls the provider and records whatever was observed. Anything that prevents the call is
/// a typed refusal that leaves the health state untouched.
///
/// The two probes are deliberately distinct: a passing connection probe confirms only that the endpoint
/// answers, so it must never be recorded as a verified recovery (ТЗ §6.10).
/// </summary>
public sealed class HealthProbeServiceTests
{
    private const string AccountId = "acc-probe";
    private const string ProviderProfileId = "prov-probe";
    private const string ProbeModelId = "mock-model-1";

    private static readonly HealthScope AccountScope = HealthScope.ForAccount(AccountId);

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryProviderProfileRepository _profiles = new();

    [Fact]
    public async Task LocalCredentialRefusal_DoesNotCountAsProviderAuthFailureOrCascade()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();
        var sibling = HealthScope.ForModelRoute(AccountId, "sibling-model");
        await health.ReportSuccessAsync(sibling);
        var before = await health.GetSnapshotAsync(AccountScope);
        var modelProbe = StubModelProbeExecutor.CredentialUnavailable();
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);
        var outcome = await probe.ProbeModelAsync(AccountScope, HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));
        Assert.Equal(HealthProbeRefusal.SecretReferenceUnavailable, outcome.Refusal);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(before.AccountedFailureCount, outcome.Snapshot.AccountedFailureCount);
        Assert.True((await health.GetSnapshotAsync(sibling)).IsRoutable);
    }

    [Fact]
    public async Task LocalConnectionCredentialRefusal_IsInconclusiveAndDoesNotQuarantineAccount()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();
        var before = await health.GetSnapshotAsync(AccountScope);
        var connection = StubConnectionTestService.Failing(ProviderConnectionStatus.SecretUnavailable);
        var probe = CreateProbeService(health, connectionTest: connection, modelProbe: null);
        var outcome = await probe.ProbeConnectionAsync(AccountScope);
        Assert.Equal(HealthProbeRefusal.SecretReferenceUnavailable, outcome.Refusal);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(before.AccountedFailureCount, outcome.Snapshot.AccountedFailureCount);
        Assert.Equal(HealthState.Recovering, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AConnectionProbeThatPasses_ConfirmsOnlyTheConnection()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 42, modelCount: 3);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.Equal(HealthProbeKind.Connection, outcome.Kind);
        Assert.Equal(HealthProbeRefusal.None, outcome.Refusal);
        Assert.Equal(42, outcome.LatencyMs);

        // The whole point: a bare GET /models does not confirm a minimal turn, so it must not be
        // recorded as a verified recovery and the scope stays out of routing.
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Recovering, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        Assert.Equal(1, connectionTest.CallCount);

        var audit = await health.GetAuditAsync(AccountScope);

        // The connection observation is audited, but nothing claims a verified recovery.
        Assert.DoesNotContain(audit, entry => entry.IsVerifiedRecovery);
        Assert.Contains(audit, entry => entry.NewState == HealthState.Recovering);
        Assert.Contains("does not verify a recovery", audit[0].Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AModelProbeThatPasses_ProducesAVerifiedRecovery()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 33);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthProbeKind.Model, outcome.Kind);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);

        // Only the pinned model probe may claim a verified recovery, and it must have run once.
        Assert.Equal(1, modelProbe.CallCount);
        Assert.Equal(ProbeModelId, modelProbe.LastRequest!.ModelId);

        var audit = await health.GetAuditAsync(AccountScope);
        Assert.True(audit[0].IsVerifiedRecovery);
    }

    [Fact]
    public async Task AModelProbeFromForcedEnabled_ProducesAVerifiedRecovery()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();
        await health.ForceEnableAsync(AccountScope, "The operator accepted the risk.");

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 17);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        // A forced route owes a verified recovery, so the pinned probe may run against it and replace
        // the unverified force with observed evidence.
        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);
        Assert.Equal(1, modelProbe.CallCount);

        var audit = await health.GetAuditAsync(AccountScope);

        // The probe start is its own audited step: ForcedEnabled -> Recovering -> Healthy.
        Assert.Contains(
            audit,
            entry => entry.PreviousState == HealthState.ForcedEnabled &&
                     entry.NewState == HealthState.Recovering);
        Assert.True(audit[0].IsVerifiedRecovery);
    }

    [Fact]
    public async Task AFailingModelProbeFromForcedEnabled_QuarantinesInsteadOfStayingForced()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();
        await health.ForceEnableAsync(AccountScope, "The operator accepted the risk.");

        var modelProbe = StubModelProbeExecutor.Failing(HealthErrorClass.NetworkOrTimeout);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        // A failed verification must not leave the scope routed on the strength of the force.
        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);
    }

    [Fact]
    public async Task AModelProbeWithoutAConfiguredExecutor_FailsClosedAsUnsupported()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var probe = CreateProbeService(health, connectionTest: null, modelProbe: null);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        // The backend cannot verify a recovery, so the scope stays out of routing instead of being
        // shown as recovered, and the caller can surface this as Unsupported.
        Assert.False(outcome.WasExecuted);
        Assert.True(outcome.IsUnsupported);
        Assert.Equal(HealthProbeRefusal.ProbeModelUnsupported, outcome.Refusal);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);

        var current = await health.GetSnapshotAsync(AccountScope);
        Assert.Equal(HealthState.ProbeRequired, current.State);
    }

    [Fact]
    public async Task AModelProbeWithoutAConfirmation_IsRefused()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 5);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: false));

        Assert.Equal(HealthProbeRefusal.ConfirmationRequired, outcome.Refusal);
        Assert.Equal(0, modelProbe.CallCount);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AFailingModelProbe_QuarantinesAndRecordsTheObservedClass()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        // An auth failure blocks every route of the account, so the account scope is quarantined. Only a
        // model mismatch is isolated on the model route instead (ТЗ §6.10).
        var modelProbe = StubModelProbeExecutor.Failing(HealthErrorClass.AuthenticationOrRefresh);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, outcome.ErrorClass);
    }

    [Fact]
    public async Task AModelProbeOnAnAccount_WhenTheProviderReportsAModelMismatch_IsolatesTheFailureOnTheModelRoute()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var modelProbe = StubModelProbeExecutor.Failing(HealthErrorClass.ModelUnavailableOrMismatch);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthErrorClass.ModelUnavailableOrMismatch, outcome.ErrorClass);

        // ТЗ §6.10: a model mismatch blocks only the concrete model route, so the account itself must not
        // be quarantined; it still owes a passing probe, but its other models are not blocked.
        var accountSnapshot = await health.GetSnapshotAsync(AccountScope);
        Assert.Equal(HealthState.Recovering, accountSnapshot.State);
        Assert.NotEqual(HealthState.QuarantinedAuto, accountSnapshot.State);
        Assert.False(accountSnapshot.IsRoutable);

        var routeScope = HealthScope.ForModelRoute(AccountId, ProbeModelId);
        var routeSnapshot = await health.GetSnapshotAsync(routeScope);
        Assert.False(routeSnapshot.IsRoutable);

        var routeAudit = await health.GetAuditAsync(routeScope);
        Assert.Contains(routeAudit, entry => entry.ErrorClass == HealthErrorClass.ModelUnavailableOrMismatch);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_ResolvesTheTargetThroughTheAccountAndRestoresTheRouteToHealthy()
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        var routeScope = HealthScope.ForModelRoute(AccountId, ProbeModelId);

        await health.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(routeScope);

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 21);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            routeScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);

        // The route scope carries only its account reference, so the endpoint could only be resolved
        // through the account and its provider profile.
        Assert.Equal(ProviderProfileId, modelProbe.LastRequest!.ProviderProfileId);
        Assert.Equal("https://provider.example/v1", modelProbe.LastRequest.BaseUrl);
        Assert.Equal(ProbeModelId, modelProbe.LastRequest.ModelId);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_WithoutARepeatedModel_ProbesTheModelNamedByTheRoute()
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        var routeScope = HealthScope.ForModelRoute(AccountId, ProbeModelId);

        await health.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(routeScope);

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 21);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(routeScope, HealthProbeConfirmation.ForModel(string.Empty, costPreviewAcknowledged: true));

        Assert.True(outcome.Succeeded);
        Assert.Equal(ProbeModelId, modelProbe.LastRequest!.ModelId);
    }

    [Theory]
    [InlineData("different-model-id")]
    [InlineData("MOCK-MODEL-1")]
    [InlineData("mock-model-1 ")]
    [InlineData(" mock-model-1")]
    [InlineData(" ")]
    public async Task AModelProbeOnAModelRoute_WhenConfirmationModelDiffers_IsRefusedAndDoesNotExecuteProbeOrRestoreRoute(string confirmationModel)
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        var routeScope = HealthScope.ForModelRoute(AccountId, ProbeModelId);

        await health.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(routeScope);

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 21);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            routeScope,
            HealthProbeConfirmation.ForModel(confirmationModel, costPreviewAcknowledged: true));

        Assert.False(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthProbeRefusal.ProbeModelUnsupported, outcome.Refusal);
        Assert.Null(modelProbe.LastRequest);

        var current = await health.GetSnapshotAsync(routeScope);
        Assert.NotEqual(HealthState.Healthy, current.State);
        Assert.False(current.IsRoutable);
    }

    [Fact]
    public async Task AModelProbeOnAModelRoute_WhenReceivingAuthenticationFailure_BlocksAccountAndAllItsRoutes()
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        var route1 = HealthScope.ForModelRoute(AccountId, "gpt-4o");
        var route2 = HealthScope.ForModelRoute(AccountId, "gpt-4o-mini");
        var route3 = HealthScope.ForModelRoute(AccountId, "claude-3-5-sonnet");

        // Set route1 to ProbeRequired
        await health.ReportFailureAsync(route1, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(route1);

        // route2 and route3 are currently Healthy routes of this account
        await health.ReportSuccessAsync(route2);
        await health.ReportSuccessAsync(route3);

        var modelProbe = StubModelProbeExecutor.Failing(HealthErrorClass.AuthenticationOrRefresh);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            route1,
            HealthProbeConfirmation.ForModel("gpt-4o", costPreviewAcknowledged: true));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, outcome.ErrorClass);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        // Account must be blocked (ТЗ §6.10)
        var accountSnapshot = await health.GetSnapshotAsync(AccountScope);
        Assert.False(accountSnapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, accountSnapshot.ErrorClass);

        // All other routes of the account must also be blocked (ТЗ §6.10)
        var route2Snapshot = await health.GetSnapshotAsync(route2);
        Assert.False(route2Snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, route2Snapshot.ErrorClass);

        var route3Snapshot = await health.GetSnapshotAsync(route3);
        Assert.False(route3Snapshot.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, route3Snapshot.ErrorClass);
    }

    [Fact]
    public async Task AModelProbeOnARouteWithoutAnAccountReference_IsRefusedAsTargetUnknown()
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        var routeScope = HealthScope.ForRoute("route-without-account");

        await health.ReportFailureAsync(routeScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(routeScope);

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 5);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            routeScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.False(outcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.ProbeTargetUnknown, outcome.Refusal);
        Assert.Equal(0, modelProbe.CallCount);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AModelProbeAgainstABackendWithNoEndpoint_IsUnsupported()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);

        await _accounts.SaveAsync(new Account(
            AccountId,
            ProviderProfileId,
            "Probe Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null));

        // A CLI-backed profile has no HTTP endpoint a pinned model probe could exercise.
        _profiles.Save(new ProviderProfile(
            ProviderProfileId,
            "Cursor Profile",
            BackendType.CursorAcp,
            null,
            @"C:\tools\cursor-agent.exe",
            DataClassification.PublicSource,
            true));

        var modelProbe = StubModelProbeExecutor.Succeeding(latencyMs: 5);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.Equal(HealthProbeRefusal.ProbeModelUnsupported, outcome.Refusal);
        Assert.Equal(0, modelProbe.CallCount);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task AFailingConnectionProbe_QuarantinesAndRecordsTheObservedErrorClass()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var connectionTest = StubConnectionTestService.Failing(ProviderConnectionStatus.AuthenticationFailed);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthState.QuarantinedAuto, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        // The audit carries the real class instead of a generic failure.
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, outcome.ErrorClass);

        var audit = await health.GetAuditAsync(AccountScope);
        Assert.False(audit[0].IsVerifiedRecovery);
    }

    [Fact]
    public async Task ProbeEvidence_ContainsTheSanitizedEndpointAndNoCredential()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync(secretReference: "urn:llmworkgui:secret:provider-key");

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 7, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        await probe.ProbeConnectionAsync(AccountScope);

        var stored = _events.Events.Last(record => record.EvidenceRedactedJson is not null);
        var evidence = stored.EvidenceRedactedJson!;

        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;

        Assert.Equal("providerConnection", root.GetProperty("probe").GetString());
        Assert.Equal(StubConnectionTestService.SanitizedEndpoint, root.GetProperty("endpoint").GetString());
        Assert.Equal(nameof(ProviderConnectionStatus.Success), root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("discoveredModelCount").GetInt32());

        // Neither the secret reference nor a resolved key may appear in the audit.
        Assert.DoesNotContain("urn:llmworkgui:secret", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", evidence, StringComparison.OrdinalIgnoreCase);

        // The service only ever forwards the opaque reference; it never resolves the key itself.
        Assert.Equal("urn:llmworkgui:secret:provider-key", connectionTest.LastSettings!.ApiKeySecretRef);
        Assert.Null(connectionTest.LastExplicitApiKey);
    }

    [Theory]
    [InlineData(ProviderConnectionStatus.RemoteServerError, HealthErrorClass.Provider4xx5xx)]
    [InlineData(ProviderConnectionStatus.EndpointNotFound, HealthErrorClass.Provider4xx5xx)]
    [InlineData(ProviderConnectionStatus.ConnectionRefused, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(ProviderConnectionStatus.TimedOut, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(ProviderConnectionStatus.SslHandshakeError, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(ProviderConnectionStatus.AuthenticationFailed, HealthErrorClass.AuthenticationOrRefresh)]
    [InlineData(ProviderConnectionStatus.InvalidUrlFormat, HealthErrorClass.StartupOrSessionCreation)]
    [InlineData(ProviderConnectionStatus.InsecureRemoteHttp, HealthErrorClass.StartupOrSessionCreation)]
    [InlineData(ProviderConnectionStatus.UnknownError, HealthErrorClass.UnknownOrAmbiguousCompletion)]
    public void EveryFailureStatus_MapsOntoTheNormativeTaxonomy(
        ProviderConnectionStatus status,
        HealthErrorClass expected)
    {
        Assert.Equal(expected, HealthProbeService.ClassifyFailure(status));
    }

    [Fact]
    public void ClassifyingASuccess_IsRejectedRatherThanMappedToAFakeFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HealthProbeService.ClassifyFailure(ProviderConnectionStatus.Success));
    }

    [Fact]
    public async Task AScopeThatOwesNoProbe_IsRefusedWithoutCallingTheProvider()
    {
        var health = CreateHealthCenter();
        await SeedProbeTargetAsync();

        // The scope is Healthy: probing it would fabricate recovery evidence nobody asked for.
        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 1, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.False(outcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.NoProbePending, outcome.Refusal);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, connectionTest.CallCount);

        // The state is unchanged and nothing was audited.
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task AScopeAwaitingAProbeWithNoResolvableTarget_IsRefusedAndStaysAwaitingAProbe()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);

        // No account and no profile were seeded, so there is no endpoint to call.
        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 1, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.False(outcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.ProbeTargetUnknown, outcome.Refusal);
        Assert.Equal(0, connectionTest.CallCount);

        // A refusal must never be recorded as a failed probe: the scope still owes its probe.
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.RequiresProbe);

        var current = await health.GetSnapshotAsync(AccountScope);
        Assert.Equal(HealthState.ProbeRequired, current.State);
    }

    [Fact]
    public async Task AProviderWithoutABaseUrl_IsRefusedAsHavingNoEndpoint()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);

        await _accounts.SaveAsync(new Account(
            AccountId,
            ProviderProfileId,
            "Probe Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null));

        // A CLI-backed profile legitimately has no HTTP endpoint.
        _profiles.Save(new ProviderProfile(
            ProviderProfileId,
            "Cursor Profile",
            BackendType.CursorAcp,
            null,
            @"C:\tools\cursor-agent.exe",
            DataClassification.PublicSource,
            true));

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 1, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.Equal(HealthProbeRefusal.ProbeTargetHasNoEndpoint, outcome.Refusal);
        Assert.Equal(0, connectionTest.CallCount);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task ABackendScope_IsRefusedBecauseAConnectionProbeCannotVerifyAProcess()
    {
        var health = CreateHealthCenter();
        var backendScope = HealthScope.ForBackend("opencode-server");

        await health.ReportFailureAsync(backendScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(backendScope);

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 1, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(backendScope);

        Assert.Equal(HealthProbeRefusal.ScopeKindNotProbeable, outcome.Refusal);
        Assert.Equal(0, connectionTest.CallCount);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task ASecondConcurrentConnectionProbeOnTheSameScope_IsRefused()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var connectionTest = StubConnectionTestService.Blocking();
        var probe = CreateProbeService(health, connectionTest);

        var firstProbe = probe.ProbeConnectionAsync(AccountScope);

        await connectionTest.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondOutcome = await probe.ProbeConnectionAsync(AccountScope);

        // Running both would interleave two observations of one scope and could record the same
        // recovery twice, so the second probe is refused rather than queued.
        Assert.False(secondOutcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.ProbeAlreadyRunning, secondOutcome.Refusal);
        Assert.Equal(1, connectionTest.CallCount);

        connectionTest.Release();

        var firstOutcome = await firstProbe.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstOutcome.WasExecuted);
        Assert.True(firstOutcome.Succeeded);
    }

    [Fact]
    public async Task ASecondConcurrentModelProbeOnTheSameScope_IsRefused()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var modelProbe = StubModelProbeExecutor.Blocking();
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var firstProbe = probe.ProbeModelAsync(AccountScope, HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        await modelProbe.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondOutcome = await probe.ProbeModelAsync(
            AccountScope,
            HealthProbeConfirmation.ForModel(ProbeModelId, costPreviewAcknowledged: true));

        Assert.False(secondOutcome.WasExecuted);
        Assert.Equal(HealthProbeRefusal.ProbeAlreadyRunning, secondOutcome.Refusal);
        Assert.Equal(1, modelProbe.CallCount);

        modelProbe.Release();

        var firstOutcome = await firstProbe.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstOutcome.WasExecuted);
        Assert.True(firstOutcome.ConfirmsVerifiedRecovery);
    }

    [Fact]
    public async Task WithoutAConnectionTestService_TheProbeIsRefusedInsteadOfAssumedToPass()
    {
        var health = CreateHealthCenter();
        await MoveToProbeRequiredAsync(health);
        await SeedProbeTargetAsync();

        var probe = CreateProbeService(health, connectionTest: null);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.Equal(HealthProbeRefusal.ProbeExecutorUnavailable, outcome.Refusal);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task ARefusal_AlwaysCarriesAnExplanation()
    {
        var health = CreateHealthCenter();
        var probe = CreateProbeService(health, connectionTest: null);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.False(outcome.WasExecuted);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Explanation));
        Assert.Null(outcome.ProbedEndpoint);
        Assert.Null(outcome.LatencyMs);
    }

    private async Task MoveToProbeRequiredAsync(HealthCenterService health)
    {
        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(AccountScope);
    }

    private async Task SeedProbeTargetAsync(string? secretReference = null)
    {
        await _accounts.SaveAsync(new Account(
            AccountId,
            ProviderProfileId,
            "Probe Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null));

        _profiles.Save(new ProviderProfile(
            ProviderProfileId,
            "Probe Provider",
            BackendType.OpenCode,
            "https://provider.example/v1",
            null,
            DataClassification.PublicSource,
            true));

        _profiles.SecretReference = secretReference;
    }

    private HealthCenterService CreateHealthCenter() =>
        new(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });

    private HealthProbeService CreateProbeService(
        IHealthCenterService health,
        IProviderConnectionTestService? connectionTest,
        IModelProbeExecutor? modelProbe = null,
        bool withAccounts = true,
        bool withProfiles = true) =>
        new(
            health,
            withProfiles ? _profiles : null,
            withAccounts ? _accounts : null,
            connectionTest,
            modelProbe);
}

/// <summary>
/// Deterministic connection-test double. It records what it was asked to call so the test can prove the
/// probe really went through the provider path and that no credential leaked into the health layer.
/// </summary>
internal sealed class StubConnectionTestService : IProviderConnectionTestService
{
    public const string SanitizedEndpoint = "https://provider.example/v1/models";

    private readonly ProviderConnectionTestResult _result;
    private readonly TaskCompletionSource<ProviderConnectionTestResult>? _completion;

    private StubConnectionTestService(ProviderConnectionTestResult result, bool blocking = false)
    {
        _result = result;
        _completion = blocking
            ? new TaskCompletionSource<ProviderConnectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
    }

    public int CallCount { get; private set; }

    public CustomProviderSettings? LastSettings { get; private set; }

    public string? LastExplicitApiKey { get; private set; }

    /// <summary>Signalled once the call is genuinely in flight, so a concurrency test can observe it.</summary>
    public TaskCompletionSource CallStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static StubConnectionTestService Succeeding(long latencyMs, int modelCount) =>
        new(ProviderConnectionTestResult.CreateSuccess(
            SanitizedEndpoint,
            latencyMs,
            Enumerable.Range(0, modelCount)
                .Select(index => new DiscoveredModelInfo($"model-{index}"))
                .ToArray()));

    public static StubConnectionTestService Failing(ProviderConnectionStatus status) =>
        new(ProviderConnectionTestResult.CreateFailure(
            status,
            SanitizedEndpoint,
            $"The provider returned {status}.",
            latencyMs: 11));

    /// <summary>Holds the call open until <see cref="Release"/> so two probes can race deterministically.</summary>
    public static StubConnectionTestService Blocking() =>
        new(
            ProviderConnectionTestResult.CreateSuccess(
                SanitizedEndpoint,
                latencyMs: 5,
                Array.Empty<DiscoveredModelInfo>()),
            blocking: true);

    public void Release() => _completion?.TrySetResult(_result);

    public Task<ProviderConnectionTestResult> TestConnectionAsync(
        CustomProviderSettings settings,
        string? explicitApiKey = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSettings = settings;
        LastExplicitApiKey = explicitApiKey;

        if (_completion is not null)
        {
            CallStarted.TrySetResult();

            return _completion.Task;
        }

        return Task.FromResult(_result);
    }
}

/// <summary>Deterministic pinned-model-probe double.</summary>
internal sealed class StubModelProbeExecutor : IModelProbeExecutor
{
    public bool CanExecute(ModelProbeRequest request) => _result.Outcome != ModelProbeOutcome.Unsupported;

    public const string SanitizedEndpoint = "https://provider.example/v1/chat/completions";

    private readonly ModelProbeResult _result;
    private readonly TaskCompletionSource<ModelProbeResult>? _completion;

    private StubModelProbeExecutor(ModelProbeResult result, bool blocking = false)
    {
        _result = result;
        _completion = blocking
            ? new TaskCompletionSource<ModelProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
    }

    public int CallCount { get; private set; }

    public ModelProbeRequest? LastRequest { get; private set; }

    /// <summary>Signalled once the call is genuinely in flight, so a concurrency test can observe it.</summary>
    public TaskCompletionSource CallStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static StubModelProbeExecutor Succeeding(long latencyMs) =>
        new(ModelProbeResult.Succeeded(SanitizedEndpoint, latencyMs));

    public static StubModelProbeExecutor Failing(HealthErrorClass failureClass) =>
        new(ModelProbeResult.Failed(failureClass, SanitizedEndpoint, 12, "The probe was rejected."));

    public static StubModelProbeExecutor Unsupported() =>
        new(ModelProbeResult.Unsupported("The backend cannot run a pinned model probe."));

    public static StubModelProbeExecutor CredentialUnavailable() =>
        new(ModelProbeResult.CredentialUnavailable("A saved local header is unavailable."));

    /// <summary>Holds the call open until <see cref="Release"/> so two probes can race deterministically.</summary>
    public static StubModelProbeExecutor Blocking() =>
        new(ModelProbeResult.Succeeded(SanitizedEndpoint, latencyMs: 5), blocking: true);

    public void Release() => _completion?.TrySetResult(_result);

    public Task<ModelProbeResult> ExecuteAsync(
        ModelProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;

        if (_completion is not null)
        {
            CallStarted.TrySetResult();

            return _completion.Task;
        }

        return Task.FromResult(_result);
    }
}
