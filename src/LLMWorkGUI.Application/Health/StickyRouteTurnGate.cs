namespace LLMWorkGUI.Application.Health;

/// <summary>Whether a sticky session may send its next turn.</summary>
public enum StickyTurnGateOutcome
{
    /// <summary>The route is routable, so the turn proceeds on the existing binding.</summary>
    Allowed,

    /// <summary>
    /// The route has left routing. The turn is not sent and the user must confirm a replacement
    /// session; the binding is never silently re-routed (ТЗ §6.5).
    /// </summary>
    BlockedRequiresReplacementSession
}

/// <summary>Decision of the sticky-route gate, with the reason the turn may not proceed.</summary>
public sealed record StickyTurnGateDecision
{
    public required StickyTurnGateOutcome Outcome { get; init; }

    public required string Explanation { get; init; }

    /// <summary>Observed health the decision was based on, or <c>null</c> when no health was consulted.</summary>
    public HealthSnapshot? Snapshot { get; init; }

    public bool IsBlocked => Outcome == StickyTurnGateOutcome.BlockedRequiresReplacementSession;
}

/// <summary>
/// The send-path gate for a sticky session. Routing's sticky rule (ТЗ §6.5) says a session bound to a
/// route that has lost eligibility must stop the next turn and offer a replacement session: the prompt
/// is not sent until the user confirms. This gate is what actually enforces that at the point of
/// dispatch, so the impacted-session projection is not merely informational.
/// </summary>
public interface IStickyRouteTurnGate
{
    Task<StickyTurnGateDecision> EvaluateAsync(
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates the gate against the normalized Health Center. Both the account scope and, when a model is
/// known, the model-route scope are consulted, and the stricter state wins: an account-wide exclusion
/// blocks every route, while a model-route exclusion blocks only that model (ТЗ §6.10). The account
/// scope is the source the routing engine also consults, so the send path and the routing decision
/// cannot disagree.
/// </summary>
public sealed class StickyRouteTurnGate : IStickyRouteTurnGate
{
    private readonly IHealthCenterService _healthCenter;

    public StickyRouteTurnGate(IHealthCenterService healthCenter)
    {
        ArgumentNullException.ThrowIfNull(healthCenter);

        _healthCenter = healthCenter;
    }

    public async Task<StickyTurnGateDecision> EvaluateAsync(
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var accountSnapshot = await _healthCenter
            .GetSnapshotAsync(HealthScope.ForAccount(accountId), cancellationToken)
            .ConfigureAwait(false);

        HealthSnapshot? routeSnapshot = null;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            routeSnapshot = await _healthCenter
                .GetSnapshotAsync(HealthScope.ForModelRoute(accountId, modelId), cancellationToken)
                .ConfigureAwait(false);
        }

        // The stricter of the two states wins: an account-wide exclusion blocks every model route, and a
        // model-route exclusion blocks only that route (ТЗ §6.10).
        if (!accountSnapshot.IsRoutable)
        {
            return Blocked(accountSnapshot, "route has left routing");
        }

        if (routeSnapshot is not null && !routeSnapshot.IsRoutable)
        {
            return Blocked(routeSnapshot, $"model route '{modelId}' has left routing");
        }

        var observed = routeSnapshot ?? accountSnapshot;
        var isForced = accountSnapshot.IsForcedWithoutVerification ||
                       routeSnapshot?.IsForcedWithoutVerification == true;

        if (isForced)
        {
            return new StickyTurnGateDecision
            {
                Outcome = StickyTurnGateOutcome.Allowed,
                Snapshot = accountSnapshot.IsForcedWithoutVerification ? accountSnapshot : routeSnapshot,
                Explanation =
                    "The route participates in routing only because it was forced without a passing " +
                    "probe, so this turn proceeds on unverified evidence."
            };
        }

        return new StickyTurnGateDecision
        {
            Outcome = StickyTurnGateOutcome.Allowed,
            Snapshot = observed,
            Explanation = $"The route is in routing ({observed.State}), so the turn may be sent."
        };
    }

    private static StickyTurnGateDecision Blocked(HealthSnapshot snapshot, string subject)
    {
        var probeNote = snapshot.RequiresProbe
            ? " A probe must pass before it can return to routing."
            : string.Empty;

        return new StickyTurnGateDecision
        {
            Outcome = StickyTurnGateOutcome.BlockedRequiresReplacementSession,
            Snapshot = snapshot,
            Explanation =
                $"The sticky session's {subject} ({snapshot.State}).{probeNote} The turn was " +
                "not sent. Confirm a replacement session to continue; the binding is never re-routed " +
                "automatically (ТЗ §6.5)."
        };
    }
}
