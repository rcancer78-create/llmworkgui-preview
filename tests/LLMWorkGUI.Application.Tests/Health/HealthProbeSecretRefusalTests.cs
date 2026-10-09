using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// The account-over-profile reference selection is the point where a scope learns which credential it
/// would be probed with. A reference that cannot be used must stop the probe there, before any request
/// is sent, and must leave the health state untouched (ADR-0005 §5.2).
/// </summary>
public sealed class HealthProbeSecretRefusalTests
{
    private const string AccountId = "acc-probe";
    private const string ProviderProfileId = "prov-probe";
    private const string ProfileReference = "urn:llmworkgui:secret:profile-key";
    private const string AccountReference = "urn:llmworkgui:secret:account-key";

    private static readonly HealthScope AccountScope = HealthScope.ForAccount(AccountId);

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryProviderProfileRepository _profiles = new();
    private readonly StubSecretLifecycle _secretLifecycle = new();

    [Fact]
    public async Task ConnectionProbe_RevokedProfileReference_IsRefusedWithoutCallingTheProvider()
    {
        var health = await CreateProbeRequiredScopeAsync();
        _profiles.SecretReference = ProfileReference;
        _secretLifecycle.StatusByReference[ProfileReference] =
            SecretReferenceStatus.Revoked(ProfileReference, SecretReferenceKind.ProviderApiKey, true);

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 20, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.Equal(HealthProbeRefusal.SecretReferenceUnavailable, outcome.Refusal);
        Assert.False(outcome.WasExecuted);
        Assert.Equal(0, connectionTest.CallCount);

        // A refusal is not an observation: the health state must stay exactly as it was.
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
    }

    [Fact]
    public async Task ModelProbe_RevokedAccountReference_IsRefusedWithoutCallingTheProvider()
    {
        var health = await CreateProbeRequiredScopeAsync();
        _profiles.SecretReference = ProfileReference;
        _secretLifecycle.StatusByReference[ProfileReference] =
            SecretReferenceStatus.Active(ProfileReference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);
        _secretLifecycle.StatusByReference[AccountReference] =
            SecretReferenceStatus.Missing(AccountReference, SecretReferenceKind.ProviderApiKey, true);

        await _accounts.SaveAsync(CreateAccount(AccountReference));
        var modelProbe = StubModelProbeExecutor.Failing(HealthErrorClass.NetworkOrTimeout);
        var probe = CreateProbeService(health, connectionTest: null, modelProbe: modelProbe);

        var outcome = await probe.ProbeModelAsync(AccountScope, HealthProbeConfirmation.ForModel("mock-model-1", costPreviewAcknowledged: true));

        // The account's own reference is the more specific one, so it is the one that has to be usable.
        Assert.Equal(HealthProbeRefusal.SecretReferenceUnavailable, outcome.Refusal);
        Assert.False(outcome.WasExecuted);
        Assert.Equal(0, modelProbe.CallCount);
        Assert.Equal(AccountReference, _secretLifecycle.LastReference);
    }

    [Fact]
    public async Task ConnectionProbe_ActiveReference_ProceedsNormally()
    {
        var health = await CreateProbeRequiredScopeAsync();
        _profiles.SecretReference = ProfileReference;
        _secretLifecycle.StatusByReference[ProfileReference] =
            SecretReferenceStatus.Active(ProfileReference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 20, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.Equal(HealthProbeRefusal.None, outcome.Refusal);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, connectionTest.CallCount);
    }

    [Fact]
    public async Task ConnectionProbe_KeylessProviderWithoutAnyReference_ProceedsNormally()
    {
        // A provider that was never given a reference stays probeable: there is nothing that could
        // have been revoked or lost.
        var health = await CreateProbeRequiredScopeAsync();
        _profiles.SecretReference = null;

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 20, modelCount: 1);
        var probe = CreateProbeService(health, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, connectionTest.CallCount);
        Assert.Null(_secretLifecycle.LastReference);
    }

    [Fact]
    public async Task ConnectionProbe_WithoutSecretLifecycle_KeepsThePreviousBehaviour()
    {
        var health = await CreateProbeRequiredScopeAsync();
        _profiles.SecretReference = ProfileReference;

        var connectionTest = StubConnectionTestService.Succeeding(latencyMs: 20, modelCount: 1);
        var probe = new HealthProbeService(health, _profiles, _accounts, connectionTest);

        var outcome = await probe.ProbeConnectionAsync(AccountScope);

        Assert.True(outcome.Succeeded);
    }

    private async Task<HealthCenterService> CreateProbeRequiredScopeAsync()
    {
        var health = new HealthCenterService(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(AccountScope);

        await _accounts.SaveAsync(CreateAccount(secretReference: null));
        _profiles.Save(new ProviderProfile(
            ProviderProfileId,
            "Probe Provider",
            BackendType.OpenCode,
            "https://provider.example/v1",
            null,
            DataClassification.PublicSource,
            true));

        return health;
    }

    private static Account CreateAccount(string? secretReference) =>
        new(
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
            null,
            secretReference: secretReference);

    private HealthProbeService CreateProbeService(
        IHealthCenterService health,
        IProviderConnectionTestService? connectionTest,
        IModelProbeExecutor? modelProbe = null) =>
        new(health, _profiles, _accounts, connectionTest, modelProbe, _secretLifecycle);

    private sealed class StubSecretLifecycle : ISecretLifecycleService
    {
        public Dictionary<string, SecretReferenceStatus> StatusByReference { get; } = new(StringComparer.Ordinal);

        public string? LastReference { get; private set; }

        public Task<SecretReferenceStatus> GetStatusAsync(string reference, CancellationToken cancellationToken = default)
        {
            LastReference = reference;

            return Task.FromResult(StatusByReference.TryGetValue(reference, out var status)
                ? status
                : SecretReferenceStatus.Active(reference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null));
        }

        public Task<SecretReferenceStatus> CreateAsync(string rawSecret, SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey, SecretReferenceOwnerBinding? owner = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RotateAsync(string reference, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RevokeAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> BindAsync(string reference, SecretReferenceOwnerBinding owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveProviderApiKeyAsync(ProviderProfile profile, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeProviderApiKeyAsync(string providerProfileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAccountSecretAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
