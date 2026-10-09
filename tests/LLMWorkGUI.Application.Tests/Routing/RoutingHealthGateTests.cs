using System;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Tests.Health;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Routing;

/// <summary>
/// Routing must ask the Health Center whether a scope may participate, instead of re-deriving the rule
/// from <see cref="Account.Health"/>. Cooldown and probe-pending scopes are the cases the persisted
/// account field cannot express, so they are the ones that prove the wiring (ROADMAP Phase 7).
/// </summary>
public sealed class RoutingHealthGateTests
{
    private const string ProviderProfileId = "prov-health";
    private const string AccountId = "acc-health";

    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryQuotaSnapshotRepository _snapshots = new();
    private readonly InMemoryHealthStateRepository _healthStates = new();
    private readonly InMemoryHealthEventRepository _healthEvents = new();
    private readonly HealthTestTimeProvider _time = new();

    [Fact]
    public async Task AnAccountTheHealthCenterHasNeverObserved_StillParticipatesInRouting()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        // A missing health record means "nothing bad observed", not "excluded".
        Assert.True(decision.IsSuccess);
        Assert.Equal(AccountId, decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task ACoolingDownScope_IsExcludedEvenThoughAccountHealthStillSaysHealthy()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);

        // The persisted account field is deliberately left Healthy: without the Health Center this
        // route would be selected, which is exactly the gap TASK-042 left open.
        await SaveAccountAsync(HealthState.Healthy);
        await health.ReportFailureAsync(HealthScope.ForAccount(AccountId), HealthErrorClass.NetworkOrTimeout);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains("CoolingDown", RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbePendingScope_IsExcludedAndTheExplanationSaysAProbeIsRequired()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await health.ReportFailureAsync(HealthScope.ForAccount(AccountId), HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(HealthScope.ForAccount(AccountId));

        var decision = await engine.SelectRouteAsync(CreateRequest());
        var reason = RejectionReasonFor(decision);

        Assert.False(decision.IsSuccess);
        Assert.Contains("ProbeRequired", reason, StringComparison.Ordinal);
        Assert.Contains("probe must pass", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AQuarantinedScope_DoesNotParticipateInRouting()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        var scope = HealthScope.ForAccount(AccountId);
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(scope);
        await health.StartProbeAsync(scope);
        await health.CompleteProbeAsync(scope, succeeded: false);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains("QuarantinedAuto", RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerifiedRecovery_RestoresTheRouteToRouting()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        var scope = HealthScope.ForAccount(AccountId);
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(scope);
        var attempt = await health.TryBeginProbeAttemptAsync(scope, true, "Pinned model admission");
        await health.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded, "Pinned model success");

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.True(decision.IsSuccess);
    }

    [Fact]
    public async Task AForcedRoute_StaysOutOfAutomaticRoutingUntilTheRequestOptsIn()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        var scope = HealthScope.ForAccount(AccountId);
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(scope);
        await health.ForceEnableAsync(scope, "The operator accepted the risk.");

        var automatic = await engine.SelectRouteAsync(CreateRequest());
        var reason = RejectionReasonFor(automatic);

        Assert.False(automatic.IsSuccess);
        Assert.Contains("ForcedEnabled", reason, StringComparison.Ordinal);
        Assert.Contains("opts in", reason, StringComparison.Ordinal);

        var optedIn = await engine.SelectRouteAsync(CreateRequest() with { OptInForcedRoute = true });

        Assert.True(optedIn.IsSuccess);
        Assert.Equal(AccountId, optedIn.SelectedAccount!.Id);
    }

    [Fact]
    public async Task OptInForcedRoute_DoesNotBypassAnAccountCooldown()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);
        await health.ReportFailureAsync(HealthScope.ForAccount(AccountId), HealthErrorClass.NetworkOrTimeout);

        var decision = await engine.SelectRouteAsync(CreateRequest() with { OptInForcedRoute = true });

        Assert.False(decision.IsSuccess);
        Assert.Contains("CoolingDown", RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForcedModelRoute_IsExcludedFromAutomaticRoutingUntilTheRequestOptsIn()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);
        await ForceEnableObservedScopeAsync(health, HealthScope.ForModelRoute(AccountId, "model-broken"));

        var broken = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-broken" });
        var working = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-working" });
        var optedIn = await engine.SelectRouteAsync(CreateRequest() with
        {
            ModelId = "model-broken",
            OptInForcedRoute = true
        });

        Assert.False(broken.IsSuccess);
        Assert.Contains("model-broken", RejectionReasonFor(broken), StringComparison.Ordinal);
        Assert.Contains("ForcedEnabled", RejectionReasonFor(broken), StringComparison.Ordinal);
        Assert.True(working.IsSuccess);
        Assert.True(optedIn.IsSuccess);
    }

    [Fact]
    public async Task APinnedForcedAccount_RemainsSelectableWithoutTheAutomaticOptIn()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);
        await ForceEnableObservedScopeAsync(health, HealthScope.ForAccount(AccountId));

        var decision = await engine.SelectRouteAsync(CreateRequest() with
        {
            Policy = RoutingPolicy.Pinned,
            PinnedAccountId = AccountId
        });

        Assert.True(decision.IsSuccess);
        Assert.Equal(AccountId, decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task ASessionStickyTurn_ContinuesOnAForcedRoute()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);
        await ForceEnableObservedScopeAsync(health, HealthScope.ForAccount(AccountId));

        var request = CreateRequest();
        var decision = await engine.SelectRouteAsync(request with
        {
            Policy = RoutingPolicy.SessionSticky,
            ExistingStickyBinding = new Domain.ValueObjects.SessionBinding(
                BackendType.OpenCode,
                ProviderProfileId,
                AccountId,
                request.ModelId,
                request.ReasoningEffort,
                request.SpeedMode,
                request.ExecutionMode)
        });

        Assert.True(decision.IsSuccess);
        Assert.Equal(AccountId, decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task APersistedQuarantine_IsStillHonouredWhenTheHealthCenterHasNoRecord()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);

        // The Health Center has observed nothing, so a Health-Center-only gate would let this through.
        await SaveAccountAsync(HealthState.QuarantinedAuto);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains("QuarantinedAuto", RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAHealthCenter_TheEngineFallsBackToThePersistedAccountHealth()
    {
        var engine = CreateEngine(healthCenter: null);
        await SaveAccountAsync(HealthState.DisabledManual);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains("DisabledManual", RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HealthState.CoolingDown, false)]
    [InlineData(HealthState.ProbeRequired, false)]
    [InlineData(HealthState.Recovering, false)]
    [InlineData(HealthState.CoolingDown, true)]
    [InlineData(HealthState.ProbeRequired, true)]
    [InlineData(HealthState.Recovering, true)]
    public async Task PersistedNonRoutableHealth_IsNotWidenedByMissingHealthRecord(
        HealthState state, bool withHealthCenter)
    {
        var engine = CreateEngine(withHealthCenter ? CreateHealthCenter() : null);
        await SaveAccountAsync(state);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains(state.ToString(), RejectionReasonFor(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionStickyTurn_IsStoppedAndRequiresAReplacementSessionWhenHealthExcludesTheRoute()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await health.ReportFailureAsync(HealthScope.ForAccount(AccountId), HealthErrorClass.NetworkOrTimeout);

        var request = CreateRequest();
        var decision = await engine.SelectRouteAsync(request with
        {
            Policy = RoutingPolicy.SessionSticky,
            ExistingStickyBinding = new Domain.ValueObjects.SessionBinding(
                BackendType.OpenCode,
                ProviderProfileId,
                AccountId,
                request.ModelId,
                request.ReasoningEffort,
                request.SpeedMode,
                request.ExecutionMode)
        });

        // ТЗ §6.5: the turn stops and the user must confirm a replacement session; it is never silently
        // re-routed to another account.
        Assert.False(decision.IsSuccess);
        Assert.True(decision.RequiresReplacementSession);
    }

    [Fact]
    public async Task WhenModelRouteIsQuarantined_OnlyThatModelRouteIsExcluded_OtherModelsForAccountRemainRoutable()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await QuarantineAsync(health, HealthScope.ForModelRoute(AccountId, "model-broken"));

        var broken = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-broken" });
        var working = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-working" });

        Assert.False(broken.IsSuccess);
        var reason = RejectionReasonFor(broken);
        Assert.Contains("model-broken", reason, StringComparison.Ordinal);
        Assert.Contains("QuarantinedAuto", reason, StringComparison.Ordinal);

        // A model mismatch or route quarantine blocks only that route: another model of the same healthy
        // account remains routable (ТЗ §6.10).
        Assert.True(working.IsSuccess);
        Assert.Equal(AccountId, working.SelectedAccount!.Id);
    }

    [Fact]
    public async Task WhenAccountIsQuarantined_AllModelRoutesForAccountAreExcluded()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await QuarantineAsync(health, HealthScope.ForAccount(AccountId));

        var first = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-a" });
        var second = await engine.SelectRouteAsync(CreateRequest() with { ModelId = "model-b" });

        // An account-wide exclusion blocks every model route, whatever model is requested.
        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Contains("account state", RejectionReasonFor(first), StringComparison.Ordinal);
        Assert.Contains("QuarantinedAuto", RejectionReasonFor(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StricterStateWins_AccountCooldownOverrulesHealthyRoute()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await health.ReportFailureAsync(HealthScope.ForAccount(AccountId), HealthErrorClass.NetworkOrTimeout);

        // The model route was never observed, so it defaults to Healthy; the account-wide cooldown is
        // the stricter state and must win (ТЗ §6.10).
        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        var reason = RejectionReasonFor(decision);
        Assert.Contains("account state", reason, StringComparison.Ordinal);
        Assert.Contains("CoolingDown", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StricterStateWins_RouteCooldownOverrulesHealthyAccount()
    {
        var health = CreateHealthCenter();
        var engine = CreateEngine(health);
        await SaveAccountAsync(HealthState.Healthy);

        await health.ReportFailureAsync(
            HealthScope.ForModelRoute(AccountId, "gpt-4o"),
            HealthErrorClass.NetworkOrTimeout);

        // The account is Healthy, but the requested route is cooling down: the stricter state wins.
        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        var reason = RejectionReasonFor(decision);
        Assert.Contains("model route 'gpt-4o'", reason, StringComparison.Ordinal);
        Assert.Contains("CoolingDown", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Failure, cooldown expiry, then the operator force-enables the scope without a passing probe.
    /// </summary>
    private async Task ForceEnableObservedScopeAsync(HealthCenterService health, HealthScope scope)
    {
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(scope);
        await health.ForceEnableAsync(scope, "The operator accepted the risk.");
    }

    /// <summary>
    /// Drives a scope through the normative recovery cycle into <see cref="HealthState.QuarantinedAuto"/>:
    /// failure, cooldown, required probe, then a failing probe.
    /// </summary>
    private async Task QuarantineAsync(HealthCenterService health, HealthScope scope)
    {
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(scope);
        await health.StartProbeAsync(scope);
        await health.CompleteProbeAsync(scope, succeeded: false);
    }

    /// <summary>
    /// The per-candidate rejection reason. Automatic policies summarize the outcome and keep the
    /// explainable detail per candidate, which is where the health gate has to be visible.
    /// </summary>
    private static string RejectionReasonFor(RoutingDecision decision)
    {
        var rejected = Assert.Single(decision.RejectedCandidates);

        Assert.Equal(AccountId, rejected.AccountId);

        return rejected.Reason;
    }

    private async Task SaveAccountAsync(HealthState health) =>
        await _accounts.SaveAsync(new Account(
            AccountId,
            ProviderProfileId,
            "Health Gate Account",
            null,
            AuthState.Valid,
            10,
            true,
            health,
            null,
            null,
            2,
            null));

    private static RouteSelectionRequest CreateRequest() =>
        new()
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = ProviderProfileId,
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

    private RoutingEngine CreateEngine(IHealthCenterService? healthCenter)
    {
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile(ProviderProfileId, "Health test provider", BackendType.OpenCode,
            null, null, DataClassification.PrivateSource, true));
        return new(
            _accounts,
            _snapshots,
            weights: null,
            timeProvider: _time,
            logger: null,
            providerProfileRepository: profiles,
            accountBridges: null,
            healthCenter: healthCenter);
    }

    private HealthCenterService CreateHealthCenter() =>
        new(_healthStates, _healthEvents, _time, new HealthPolicy { FailureThreshold = 1 });
}
