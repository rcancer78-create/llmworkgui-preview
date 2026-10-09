using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Read-only projection of the sessions affected by one scope's health. It classifies each session and
/// explains what the operator must do, but it never cancels a turn, closes a session or changes health:
/// a replacement session always requires explicit user confirmation (ТЗ §6.5).
/// </summary>
public sealed class ImpactedSessionService : IImpactedSessionService
{
    private readonly IHealthCenterService _healthCenter;
    private readonly ISessionRepository? _sessions;

    public ImpactedSessionService(
        IHealthCenterService healthCenter,
        ISessionRepository? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(healthCenter);

        _healthCenter = healthCenter;
        _sessions = sessions;
    }

    public async Task<ImpactedSessionReport> GetImpactedSessionsAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var snapshot = await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);

        if (_sessions is null)
        {
            // An empty list would read as "nothing is affected", which is not what was observed.
            return Incomplete(
                snapshot,
                "No session repository is configured, so the affected sessions could not be determined.");
        }

        // Only an account scope can be resolved to sessions: a session's binding carries an account id,
        // not a route or backend id.
        if (scope.ScopeType != HealthScope.AccountScopeType)
        {
            return Incomplete(
                snapshot,
                $"A {scope.ScopeType} scope cannot be resolved to sessions, because a session binding " +
                "references an account rather than a route or backend.");
        }

        var sessions = await _sessions
            .ListByAccountAsync(scope.ScopeId, cancellationToken)
            .ConfigureAwait(false);

        var impacted = sessions
            .Select(session => Classify(session, snapshot))
            .Where(session => session.Impact != SessionImpact.None)
            .ToList();

        return new ImpactedSessionReport
        {
            Scope = scope,
            State = snapshot.State,
            IsRoutable = snapshot.IsRoutable,
            Sessions = impacted,
            IsComplete = true,
            Summary = BuildSummary(snapshot, impacted)
        };
    }

    /// <summary>
    /// Classifies one session against the observed health of its route. The order of the checks matters:
    /// an unresolved outcome is decided by reconciliation, never by health, so it is tested first.
    /// </summary>
    private static ImpactedSession Classify(Session session, HealthSnapshot snapshot)
    {
        var impact = DetermineImpact(session, snapshot);

        return new ImpactedSession
        {
            SessionId = session.Id,
            ProjectId = session.ProjectId,
            State = session.State,
            Impact = impact,
            NativeSessionId = session.NativeSessionId,
            ActiveExecutionId = session.ActiveExecutionId,
            Role = session.Role,
            LastEventAt = session.LastEventAt,
            Explanation = Explain(impact, session)
        };
    }

    private static SessionImpact DetermineImpact(Session session, HealthSnapshot snapshot)
    {
        // A closed session cannot be harmed further, whatever the route now does.
        if (session.State == SessionState.Closed)
        {
            return SessionImpact.None;
        }

        // Ambiguous and Orphaned outcomes belong to reconciliation. Health must not resolve them in
        // either direction, because that would either lose or duplicate applied work.
        if (session.State is SessionState.Ambiguous or SessionState.Orphaned)
        {
            return SessionImpact.AwaitingReconciliation;
        }

        if (snapshot.IsForcedWithoutVerification)
        {
            return SessionImpact.ProceedingOnUnverifiedRoute;
        }

        if (snapshot.IsRoutable)
        {
            // The route is healthy or merely degraded, so the session is not impacted.
            return SessionImpact.None;
        }

        // The route has left routing. A turn already in flight cannot be stopped from here, so that is a
        // recommendation only; a session that is merely idle has its *next* turn refused by the send
        // path until the route recovers.
        return session.ActiveExecutionId is not null
            ? SessionImpact.RunningTurnRequiresStop
            : SessionImpact.NextTurnBlocked;
    }

    private static string Explain(SessionImpact impact, Session session) => impact switch
    {
        SessionImpact.RunningTurnRequiresStop =>
            $"Execution '{session.ActiveExecutionId}' is running on a route that has left routing. Stop the " +
            "turn and confirm a replacement session: this is a recommendation, because nothing here cancels " +
            "the running turn, and a replacement session is never created automatically.",

        SessionImpact.NextTurnBlocked =>
            "The session is bound to a route that has left routing, so the next turn is refused by the " +
            "Workspace and will not be sent until the route recovers. No work has been lost.",

        SessionImpact.AwaitingReconciliation =>
            $"The session outcome is {session.State} and belongs to reconciliation. The health state must not " +
            "decide it in either direction.",

        SessionImpact.ProceedingOnUnverifiedRoute =>
            "The route was forced back into routing without a passing probe, so any further turn in this " +
            "session proceeds on unverified evidence.",

        SessionImpact.None =>
            "The session is not affected by the current health state of this route.",

        _ => "Not reported."
    };

    private static string BuildSummary(HealthSnapshot snapshot, IReadOnlyList<ImpactedSession> sessions)
    {
        if (sessions.Count == 0)
        {
            return $"No session is affected by the current {snapshot.State} state of this scope.";
        }

        var stopped = sessions.Count(session => session.RequiresReplacementSession);
        var blocked = sessions.Count(session => session.Impact == SessionImpact.NextTurnBlocked);
        var unresolved = sessions.Count(session => session.Impact == SessionImpact.AwaitingReconciliation);
        var unverified = sessions.Count(session => session.Impact == SessionImpact.ProceedingOnUnverifiedRoute);

        var parts = new List<string>();

        if (stopped > 0)
        {
            parts.Add($"{stopped} mid-turn with a running turn that must be stopped and a replacement session confirmed");
        }

        if (blocked > 0)
        {
            parts.Add($"{blocked} whose next turn is refused until the route recovers");
        }

        if (unresolved > 0)
        {
            parts.Add($"{unresolved} awaiting reconciliation");
        }

        if (unverified > 0)
        {
            parts.Add($"{unverified} proceeding on an unverified route");
        }

        return $"{sessions.Count} affected session(s): {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// A report that states its own incompleteness. This is deliberately not an empty successful report,
    /// so the UI cannot show "nothing is affected" for something it never managed to look up.
    /// </summary>
    private static ImpactedSessionReport Incomplete(HealthSnapshot snapshot, string reason) =>
        new()
        {
            Scope = snapshot.Scope,
            State = snapshot.State,
            IsRoutable = snapshot.IsRoutable,
            IsComplete = false,
            Summary = reason
        };
}
