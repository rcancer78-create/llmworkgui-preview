using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.Entities;

public sealed class Route
{
    public Route(
        string id,
        SessionBinding binding,
        DataClassification maxDataClass,
        bool isEnabled,
        HealthState health,
        int manualPriority,
        string? gatewayRouteKey = null)
    {
        if (manualPriority < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(manualPriority), "Manual priority must not be negative.");
        }

        Id = DomainGuard.NotBlank(id, nameof(id));
        ArgumentNullException.ThrowIfNull(binding);
        Binding = binding;
        MaxDataClass = maxDataClass;
        IsEnabled = isEnabled;
        Health = health;
        ManualPriority = manualPriority;
        GatewayRouteKey = DomainGuard.OptionalNotBlank(gatewayRouteKey, nameof(gatewayRouteKey));
    }

    public string Id { get; }

    public SessionBinding Binding { get; }

    public DataClassification MaxDataClass { get; }

    public bool IsEnabled { get; }

    public HealthState Health { get; }

    public int ManualPriority { get; }

    /// <summary>
    /// One gateway route key, when the gateway reports a single key instead of separate mode dimensions.
    /// <para>
    /// This is the gateway's own namespace and has nothing to do with <see cref="Id"/>. Two systems can
    /// independently mint the same string, so a gateway route key that happens to equal a persisted
    /// <c>Routes.Id</c> is a coincidence and must never be treated as an identity match. Null means no
    /// gateway route key is recorded for this route.
    /// </para>
    /// </summary>
    public string? GatewayRouteKey { get; }
}
