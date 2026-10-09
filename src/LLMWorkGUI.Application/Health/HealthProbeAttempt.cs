namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Local ownership of one admitted probe, not native identity or a durable execution identifier.
/// Any later health mutation invalidates its authority to recover the scope.
/// </summary>
public sealed record HealthProbeAttempt(string Id, HealthScope Scope);

public enum HealthProbeCompletionKind
{
    ConnectionSucceeded,
    ModelSucceeded,
    Failed,
    Inconclusive,
    AuthenticationFailed
}

public sealed record HealthProbeAttemptCompletion(HealthSnapshot Snapshot, bool WasCurrent, bool VerifiedRecovery);
