using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Resolves the circuit-breaker policy for a scope. The breaker parameters are configurable per
/// provider and per scope, while the normative defaults (ТЗ §6.10) stay N=3 identical failures inside
/// W=15 minutes with a D=5 minute cooldown plus the immediate auth/model-mismatch block.
/// </summary>
public interface IHealthPolicyProvider
{
    HealthPolicy GetPolicy(HealthScope scope);
}

/// <summary>
/// Configuration surface for <see cref="HealthPolicyRegistry"/>. Overrides are keyed by scope id
/// (for example a provider profile id), by <c>scopeType:scopeId</c>, or by scope type. Anything not
/// overridden uses <see cref="Default"/>, which carries the normative values.
/// </summary>
public sealed class HealthPolicyOptions
{
    public HealthPolicy Default { get; set; } = new();

    public Dictionary<string, HealthPolicy> ScopedPolicies { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Applies the configured per-scope overrides on top of the normative defaults. The lookup order is
/// most specific first: exact scope id, then <c>scopeType:scopeId</c>, then scope type, then default.
/// </summary>
public sealed class HealthPolicyRegistry : IHealthPolicyProvider
{
    private readonly HealthPolicyOptions _options;

    public HealthPolicyRegistry(HealthPolicyOptions? options = null)
    {
        _options = options ?? new HealthPolicyOptions();
    }

    public HealthPolicy GetPolicy(HealthScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (_options.ScopedPolicies.TryGetValue(scope.ScopeId, out var byScopeId))
        {
            return byScopeId;
        }

        if (_options.ScopedPolicies.TryGetValue($"{scope.ScopeType}:{scope.ScopeId}", out var byQualifiedId))
        {
            return byQualifiedId;
        }

        if (_options.ScopedPolicies.TryGetValue(scope.ScopeType, out var byScopeType))
        {
            return byScopeType;
        }

        return _options.Default;
    }
}
