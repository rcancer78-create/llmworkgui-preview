using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// Recovery and audit behaviour (ROADMAP Phase 7 exit criteria): a manual probe owns its own
/// evidence, a forced enable is distinguishable from a verified recovery, and every transition lands
/// in the append-only audit.
/// </summary>
public sealed class HealthCenterRecoveryTests
{
    private static readonly HealthScope Scope = HealthScope.ForAccount("account-1");

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();

    [Fact]
    public async Task ManualProbe_ThatPasses_ProducesAVerifiedRecovery()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);

        var attempt = await service.TryBeginProbeAttemptAsync(Scope, true, "A pinned model probe was admitted.");
        Assert.NotNull(attempt);
        var probing = await service.GetSnapshotAsync(Scope);

        // A started probe is not yet a recovery: the scope stays out of routing until it passes.
        Assert.Equal(HealthState.Recovering, probing.State);
        Assert.False(probing.IsRoutable);

        var recovered = (await service.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded,
            "A pinned model answered the minimal operation.", """{"probe":"model","status":200}""")).Snapshot;

        Assert.Equal(HealthState.Healthy, recovered.State);
        Assert.True(recovered.IsRoutable);
        Assert.False(recovered.IsForcedWithoutVerification);
        Assert.Equal(0, recovered.AccountedFailureCount);

        var audit = await service.GetAuditAsync(Scope);
        var recovery = audit.First();

        Assert.True(recovery.IsVerifiedRecovery);
        Assert.False(recovery.IsForcedRecovery);
        Assert.Equal(HealthState.Recovering, recovery.PreviousState);
    }

    [Fact]
    public async Task ManualProbe_ThatFails_QuarantinesInsteadOfRestoringRouting()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);

        await service.StartProbeAsync(Scope);
        var snapshot = await service.CompleteProbeAsync(Scope, succeeded: false);

        Assert.Equal(HealthState.QuarantinedAuto, snapshot.State);
        Assert.False(snapshot.IsRoutable);
        Assert.True(snapshot.RequiresProbe);
    }

    [Fact]
    public async Task ManualProbe_HasItsOwnAuditedStepsAndEvidence()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);

        var attempt = await service.TryBeginProbeAttemptAsync(Scope, true, "Pinned model admission");
        await service.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded,
            "Pinned model success", """{"probe":"ok"}""");

        var audit = await service.GetAuditAsync(Scope);

        // Start and completion are separate auditable steps, so a probe cannot be claimed without
        // having been run.
        Assert.Contains(audit, entry => entry.NewState == HealthState.Recovering);
        Assert.Contains(audit, entry => entry.IsVerifiedRecovery);
    }

    [Fact]
    public async Task ForceEnable_IsRoutableButNeverCountsAsAVerifiedRecovery()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);

        var forced = await service.ForceEnableAsync(Scope, "The operator accepted the risk.");

        // It participates in routing, but the UI and the audit must both show it is unverified.
        Assert.Equal(HealthState.ForcedEnabled, forced.State);
        Assert.True(forced.IsRoutable);
        Assert.True(forced.IsForcedWithoutVerification);

        var entry = (await service.GetAuditAsync(Scope)).First();

        Assert.True(entry.IsForcedRecovery);
        Assert.False(entry.IsVerifiedRecovery);
        Assert.Contains("without a passing probe", entry.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForceEnable_ThenProbeSuccess_BecomesAVerifiedRecovery()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);
        await service.ForceEnableAsync(Scope, "The operator accepted the risk.");

        var attempt = await service.TryBeginProbeAttemptAsync(Scope, true, "Pinned model admission");
        var verified = (await service.CompleteProbeAttemptAsync(attempt!,
            HealthProbeCompletionKind.ModelSucceeded, "Pinned model success")).Snapshot;

        // Only now is the recovery backed by evidence, and the state stops being ForcedEnabled.
        Assert.Equal(HealthState.Healthy, verified.State);
        Assert.False(verified.IsForcedWithoutVerification);
    }

    [Fact]
    public async Task RequireProbe_OnAForcedScope_MovesItOutOfRoutingUntilTheProbePasses()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);
        await service.ForceEnableAsync(Scope, "The operator accepted the risk.");

        var required = await service.RequireProbeAsync(Scope, "The forced route must still be verified.");

        // A forced route can be sent back to the probe it never passed.
        Assert.Equal(HealthState.ProbeRequired, required.State);
        Assert.False(required.IsRoutable);
        Assert.True(required.RequiresProbe);
        Assert.False(required.IsForcedWithoutVerification);
    }

    [Fact]
    public async Task RequireProbe_OnAScopeThatWasNotForced_FailsLoudlyInsteadOfPretending()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(() => service.RequireProbeAsync(Scope));
    }

    [Fact]
    public async Task AuthenticationFailure_OnAForcedAccount_BlocksTheAccountAndItsForcedModelRoutes()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });
        var route = HealthScope.ForModelRoute("account-1", "gpt-4o");
        var otherAccountRoute = HealthScope.ForModelRoute("account-2", "gpt-4o");

        // Both the account and its route were forced back into routing without a passing probe.
        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        await service.ForceEnableAsync(Scope, "The operator accepted the risk.");
        await service.ReportFailureAsync(route, HealthErrorClass.NetworkOrTimeout);
        await service.ForceEnableAsync(route, "The operator accepted the risk.");
        await service.ReportSuccessAsync(otherAccountRoute);

        var outcome = await service.ReportFailureAsync(Scope, HealthErrorClass.AuthenticationOrRefresh);

        // ТЗ §6.10: an auth failure blocks the forced account immediately, and the block cascades to
        // the model routes the account owns.
        Assert.Equal(HealthState.CoolingDown, outcome.Snapshot.State);
        Assert.False(outcome.Snapshot.IsRoutable);

        var routeSnapshot = await service.GetSnapshotAsync(route);
        Assert.False(routeSnapshot.IsRoutable);
        Assert.Equal(HealthState.CoolingDown, routeSnapshot.State);

        var routeAudit = await service.GetAuditAsync(route);
        Assert.Contains(routeAudit, entry => entry.ErrorClass == HealthErrorClass.AuthenticationOrRefresh);

        // A route of another account is not touched by the cascade.
        var otherSnapshot = await service.GetSnapshotAsync(otherAccountRoute);
        Assert.True(otherSnapshot.IsRoutable);
    }

    [Fact]
    public async Task DisableManually_RemovesTheScopeFromRoutingWithTheOperatorReason()
    {
        var service = CreateService();

        var disabled = await service.DisableManuallyAsync(Scope, "Disabled while the provider migrates.");

        Assert.Equal(HealthState.DisabledManual, disabled.State);
        Assert.False(disabled.IsRoutable);

        var entry = (await service.GetAuditAsync(Scope)).First();
        Assert.Equal("Disabled while the provider migrates.", entry.Reason);
    }

    [Fact]
    public async Task Enable_AfterAManualDisable_RequiresAProbeInsteadOfBecomingHealthy()
    {
        var service = CreateService();
        await service.DisableManuallyAsync(Scope, "Temporarily disabled.");

        var enabled = await service.EnableAsync(Scope, "The provider migration finished.");

        // Re-enabling is an intention, not evidence that the route works.
        Assert.Equal(HealthState.ProbeRequired, enabled.State);
        Assert.False(enabled.IsRoutable);
        Assert.True(enabled.RequiresProbe);
    }

    [Fact]
    public async Task EveryTransition_IsRecordedInTheAuditWithItsPreviousState()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });

        await service.ReportFailureAsync(Scope, HealthErrorClass.AuthenticationOrRefresh);
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
        var attempt = await service.TryBeginProbeAttemptAsync(Scope, true, "Pinned model admission");
        await service.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded, "Pinned model success");

        var audit = await service.GetAuditAsync(Scope);

        // Newest first: Healthy <- Recovering <- ProbeRequired <- CoolingDown.
        Assert.Equal(4, audit.Count);
        Assert.Collection(
            audit.Reverse(),
            entry =>
            {
                // The very first observation of a scope has no previous state. Reporting Healthy here
                // would claim an observation that never happened.
                Assert.Null(entry.PreviousState);
                Assert.Equal(HealthState.CoolingDown, entry.NewState);
                Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, entry.ErrorClass);
            },
            entry =>
            {
                Assert.Equal(HealthState.CoolingDown, entry.PreviousState);
                Assert.Equal(HealthState.ProbeRequired, entry.NewState);
            },
            entry =>
            {
                Assert.Equal(HealthState.ProbeRequired, entry.PreviousState);
                Assert.Equal(HealthState.Recovering, entry.NewState);
            },
            entry =>
            {
                Assert.Equal(HealthState.Recovering, entry.PreviousState);
                Assert.Equal(HealthState.Healthy, entry.NewState);
                Assert.True(entry.IsVerifiedRecovery);
            });
    }

    [Fact]
    public async Task GetAuditAsync_WithALimit_ReturnsOnlyTheNewestEntries()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 10 });

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        }

        var limited = await service.GetAuditAsync(Scope, limit: 2);

        Assert.Equal(2, limited.Count);
        Assert.True(limited[0].OccurredAt >= limited[1].OccurredAt);
    }

    [Fact]
    public async Task ListAsync_ReportsEveryObservedScopeIndependently()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });
        var other = HealthScope.ForBackend("backend-1");

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        await service.DisableManuallyAsync(other, "Manually disabled.");

        var snapshots = await service.ListAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(
            HealthState.CoolingDown,
            snapshots.Single(snapshot => snapshot.Scope == Scope).State);
        Assert.Equal(
            HealthState.DisabledManual,
            snapshots.Single(snapshot => snapshot.Scope == other).State);
    }

    [Fact]
    public async Task StartProbe_WhenNoProbeIsPending_FailsLoudlyInsteadOfPretending()
    {
        var service = CreateService();

        // The scope is Healthy, so there is nothing to probe. Silently reporting a started probe
        // would fabricate recovery evidence.
        await Assert.ThrowsAnyAsync<Exception>(() => service.StartProbeAsync(Scope));
    }

    /// <summary>Drives a scope to <see cref="HealthState.ProbeRequired"/> through observed steps only.</summary>
    private async Task MoveToProbeRequiredAsync(HealthCenterService service)
    {
        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
    }

    private HealthCenterService CreateService(HealthPolicy? policy = null) =>
        new(_states, _events, _time, policy ?? new HealthPolicy { FailureThreshold = 1 });
}
