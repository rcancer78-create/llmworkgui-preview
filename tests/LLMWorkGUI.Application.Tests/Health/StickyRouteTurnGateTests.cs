using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// The send-path gate must apply the stricter of the account and model-route health states: an
/// account-wide exclusion blocks the turn, and a model-route exclusion blocks the turn even when the
/// account itself is healthy (ТЗ §6.10).
/// </summary>
public sealed class StickyRouteTurnGateTests
{
    private const string AccountId = "acc-sticky";
    private const string ModelId = "model-sticky";

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();

    [Fact]
    public async Task EvaluateAsync_WhenTheModelRouteIsUnhealthy_BlocksTheTurnEvenThoughTheAccountIsHealthy()
    {
        var health = CreateHealthCenter();
        var gate = new StickyRouteTurnGate(health);

        await health.ReportFailureAsync(
            HealthScope.ForModelRoute(AccountId, ModelId),
            HealthErrorClass.ModelUnavailableOrMismatch);

        var decision = await gate.EvaluateAsync(AccountId, ModelId);

        Assert.True(decision.IsBlocked);
        Assert.Equal(StickyTurnGateOutcome.BlockedRequiresReplacementSession, decision.Outcome);
        Assert.Contains(ModelId, decision.Explanation, StringComparison.Ordinal);
        Assert.Equal(HealthState.CoolingDown, decision.Snapshot!.State);

        // A model-route exclusion blocks only that model: another model of the same account passes.
        var other = await gate.EvaluateAsync(AccountId, "model-other");
        Assert.False(other.IsBlocked);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheAccountIsUnhealthy_BlocksTheTurnForEveryModel()
    {
        var health = CreateHealthCenter();
        var gate = new StickyRouteTurnGate(health);

        await health.ReportFailureAsync(
            HealthScope.ForAccount(AccountId),
            HealthErrorClass.AuthenticationOrRefresh);

        var decision = await gate.EvaluateAsync(AccountId, ModelId);

        Assert.True(decision.IsBlocked);
        Assert.Equal(StickyTurnGateOutcome.BlockedRequiresReplacementSession, decision.Outcome);
        Assert.Equal(HealthState.CoolingDown, decision.Snapshot!.State);
        Assert.Contains("route has left routing", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_WhenBothTheAccountAndTheModelRouteAreHealthy_AllowsTheTurn()
    {
        var health = CreateHealthCenter();
        var gate = new StickyRouteTurnGate(health);

        var decision = await gate.EvaluateAsync(AccountId, ModelId);

        Assert.False(decision.IsBlocked);
        Assert.Equal(StickyTurnGateOutcome.Allowed, decision.Outcome);
        Assert.Equal(HealthState.Healthy, decision.Snapshot!.State);
    }

    private HealthCenterService CreateHealthCenter() =>
        new(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });
}
