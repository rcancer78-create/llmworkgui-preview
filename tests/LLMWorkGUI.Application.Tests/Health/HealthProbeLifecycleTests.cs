using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

public sealed class HealthProbeLifecycleTests
{
    private const string AccountId = "probe-lifecycle-account";
    private const string ModelId = "probe-lifecycle-model";
    private static readonly HealthScope Scope = HealthScope.ForAccount(AccountId);
    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryProviderProfileRepository _profiles = new();
    private readonly HealthTestTimeProvider _time = new();

    private async Task<HealthCenterService> ReadyAsync()
    {
        await _accounts.SaveAsync(new Account(AccountId, "probe-lifecycle-provider", "Probe fixture", null,
            AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        _profiles.Save(new ProviderProfile("probe-lifecycle-provider", "Probe provider", BackendType.OpenCode,
            "https://provider.example/v1", null, DataClassification.PublicSource, true));
        var health = new HealthCenterService(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });
        await health.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(Scope);
        return health;
    }

    [Fact]
    public async Task ModelProbe_StartIsDurableWhileTheProviderCallIsInFlight()
    {
        var health = await ReadyAsync();
        var executor = StubModelProbeExecutor.Blocking();
        var probe = new HealthProbeService(health, _profiles, _accounts, modelProbe: executor);
        var running = probe.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true));
        await executor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var during = await health.GetSnapshotAsync(Scope);
        var startWasAudited = _events.Events.Any(e => e.NewState == HealthState.Recovering);
        executor.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthState.Recovering, during.State);
        Assert.True(startWasAudited);
    }

    [Fact]
    public async Task ConnectionProbe_StartIsDurableWhileTheProviderCallIsInFlight()
    {
        var health = await ReadyAsync();
        var executor = StubConnectionTestService.Blocking();
        var probe = new HealthProbeService(health, _profiles, _accounts, connectionTest: executor);
        var running = probe.ProbeConnectionAsync(Scope);
        await executor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var during = await health.GetSnapshotAsync(Scope);
        var startWasAudited = _events.Events.Any(e => e.NewState == HealthState.Recovering);
        executor.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthState.Recovering, during.State);
        Assert.True(startWasAudited);
    }

    [Fact]
    public async Task ModelProbe_LateSuccessAfterManualDisable_PreservesDisableAndDoesNotClaimRecovery()
    {
        var health = await ReadyAsync();
        var executor = StubModelProbeExecutor.Blocking();
        var probe = new HealthProbeService(health, _profiles, _accounts, modelProbe: executor);
        var running = probe.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true));
        await executor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await health.DisableManuallyAsync(Scope, "Disabled while probe was in flight.");
        executor.Release();

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.WasExecuted);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.DisabledManual, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);
        Assert.Contains(_events.Events, e => e.EvidenceRedactedJson is not null);
    }

    [Fact]
    public async Task ModelProbe_CancelledCallerAfterDispatch_StillLeavesStartAndTerminalObservation()
    {
        var health = await ReadyAsync();
        var executor = StubModelProbeExecutor.Blocking();
        var probe = new HealthProbeService(health, _profiles, _accounts, modelProbe: executor);
        using var cancellation = new CancellationTokenSource();
        var running = probe.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true), cancellation.Token);
        await executor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        executor.Release();
        HealthProbeOutcome? outcome = null;
        try { outcome = await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Assert.Contains(_events.Events, e => e.NewState == HealthState.Recovering);
        Assert.Contains(_events.Events, e => e.EvidenceRedactedJson is not null);
        Assert.NotEqual(HealthState.Healthy, (await health.GetSnapshotAsync(Scope)).State);
        Assert.False(outcome?.ConfirmsVerifiedRecovery == true);
    }

    [Fact]
    public async Task ModelProbe_SupersededAttemptCannotCompleteANewerRecoveringAttempt()
    {
        var health = await ReadyAsync();
        var firstExecutor = StubModelProbeExecutor.Blocking();
        var firstService = new HealthProbeService(health, _profiles, _accounts, modelProbe: firstExecutor);
        var first = firstService.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true));
        await firstExecutor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await health.DisableManuallyAsync(Scope, "Supersede the first attempt.");
        await health.EnableAsync(Scope, "A new recovery check is required.");
        // A replacement service shares the singleton Health Center. Both attempts now observe the
        // same Recovering enum, so checking state alone cannot establish ownership of the result.
        var secondExecutor = StubModelProbeExecutor.Blocking();
        var secondService = new HealthProbeService(health, _profiles, _accounts, modelProbe: secondExecutor);
        var second = secondService.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true));
        await secondExecutor.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstExecutor.Release();
        var oldOutcome = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var between = await health.GetSnapshotAsync(Scope);
        secondExecutor.Release();
        var currentOutcome = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(oldOutcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Recovering, between.State);
        Assert.True(currentOutcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, currentOutcome.Snapshot.State);
        Assert.Single((await health.GetAuditAsync(Scope)).Where(e => e.IsVerifiedRecovery));
    }

    [Fact]
    public async Task UnsupportedModelExecutor_LeavesHealthStateAndAuditUntouched()
    {
        var health = await ReadyAsync();
        var beforeWrites = _states.UpsertCount;
        var beforeEvents = _events.Events.Count;
        var probe = new HealthProbeService(health, _profiles, _accounts, modelProbe: StubModelProbeExecutor.Unsupported());

        var outcome = await probe.ProbeModelAsync(Scope, HealthProbeConfirmation.ForModel(ModelId, costPreviewAcknowledged: true));

        Assert.True(outcome.IsUnsupported);
        Assert.False(outcome.WasExecuted);
        Assert.Equal(HealthState.ProbeRequired, outcome.Snapshot.State);
        Assert.Equal(beforeWrites, _states.UpsertCount);
        Assert.Equal(beforeEvents, _events.Events.Count);
    }
}
