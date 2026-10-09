using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// What the operator must do about one session whose route is unhealthy. This is a recommendation
/// derived from observed state, never an action taken on the user's behalf.
/// </summary>
public enum SessionImpact
{
    /// <summary>
    /// The session is already finished, so the unhealthy route cannot affect it any further.
    /// </summary>
    None,

    /// <summary>
    /// The session is running a turn on a route that has left routing. This projection is a
    /// <em>recommendation</em>: nothing in this service stops the running turn, and a replacement
    /// session requires explicit user confirmation. The turn is never silently re-routed (ТЗ §6.5).
    /// </summary>
    RunningTurnRequiresStop,

    /// <summary>
    /// The session is bound to the unhealthy route but is not mid-turn. This is the case the send path
    /// actually enforces: the next turn is refused (not sent) until the route recovers, so no work is
    /// lost yet.
    /// </summary>
    NextTurnBlocked,

    /// <summary>
    /// The session outcome was never resolved (Ambiguous or Orphaned). Reconciliation owns it, and the
    /// health state must not be used to decide it either way.
    /// </summary>
    AwaitingReconciliation,

    /// <summary>
    /// The route is usable only because an operator forced it without a passing probe, so any further
    /// turn proceeds on unverified evidence.
    /// </summary>
    ProceedingOnUnverifiedRoute
}

/// <summary>One session affected by the health state of a scope.</summary>
public sealed record ImpactedSession
{
    public required string SessionId { get; init; }

    public required string ProjectId { get; init; }

    public required SessionState State { get; init; }

    public required SessionImpact Impact { get; init; }

    /// <summary>Native backend session id, or <c>null</c> when the backend never confirmed one.</summary>
    public string? NativeSessionId { get; init; }

    /// <summary>Execution currently running in this session, or <c>null</c> when none is.</summary>
    public string? ActiveExecutionId { get; init; }

    public string? Role { get; init; }

    public DateTimeOffset LastEventAt { get; init; }

    /// <summary>True when a turn is in flight and therefore at risk right now.</summary>
    public bool HasTurnInFlight => ActiveExecutionId is not null;

    /// <summary>True when the user must confirm a replacement session before work can continue.</summary>
    public bool RequiresReplacementSession =>
        Impact == SessionImpact.RunningTurnRequiresStop;

    /// <summary>Explanation shown to the operator, always populated.</summary>
    public required string Explanation { get; init; }
}

/// <summary>
/// Sessions affected by the health state of one scope, together with the reason the scope is a problem.
/// </summary>
public sealed record ImpactedSessionReport
{
    public required HealthScope Scope { get; init; }

    public required HealthState State { get; init; }

    /// <summary>False when the scope has left routing, which is why its sessions are impacted.</summary>
    public required bool IsRoutable { get; init; }

    public IReadOnlyList<ImpactedSession> Sessions { get; init; } = Array.Empty<ImpactedSession>();

    /// <summary>Sessions whose turn was stopped and which need a replacement session.</summary>
    public IReadOnlyList<ImpactedSession> RequiringReplacementSession =>
        Sessions.Where(session => session.RequiresReplacementSession).ToList();

    public bool HasImpactedSessions => Sessions.Count > 0;

    /// <summary>
    /// True when the scope resolution itself is unknown, so the list may be incomplete. The UI must say
    /// so rather than presenting an empty list as "nothing is affected".
    /// </summary>
    public required bool IsComplete { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// Answers "which sessions does this unhealthy scope affect, and what must the user do?"
/// (ROADMAP Phase 7: impacted-session view, sticky-session stop behaviour for an unhealthy route).
///
/// The service is strictly read-only: it never cancels a turn, closes a session or changes health.
/// Stopping a turn is the caller's decision, and creating a replacement session always requires
/// explicit user confirmation (ТЗ §6.5).
/// </summary>
public interface IImpactedSessionService
{
    Task<ImpactedSessionReport> GetImpactedSessionsAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default);
}
