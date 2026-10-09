namespace LLMWorkGUI.Domain.StateMachines;

public sealed record HealthPolicy
{
    /// <summary>Normative default: N = 3 identical accounted failures inside the rolling window (ТЗ §6.10).</summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>Normative default: W = 15 minutes (ТЗ §6.10).</summary>
    public TimeSpan RollingWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Normative default: D = 5 minutes (ТЗ §6.10).</summary>
    public TimeSpan CooldownDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When true, an authentication/refresh failure and a model unavailable/mismatch failure block the
    /// scope immediately on the first observation instead of waiting for <see cref="FailureThreshold"/>
    /// identical errors (ТЗ §6.10: "Auth failure немедленно блокирует все routes account. Model mismatch
    /// блокирует только конкретный route."). The caller decides the blast radius by choosing the scope:
    /// an auth failure is reported against the account scope, a model mismatch against the route scope.
    /// </summary>
    public bool BlocksImmediatelyOnAuthenticationOrModelMismatch { get; init; } = true;
}
