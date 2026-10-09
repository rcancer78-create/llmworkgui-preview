using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Request parameters for selecting a route and account (ТЗ §6.5, ADR-0004 §6).
/// </summary>
public sealed record RouteSelectionRequest
{
    public required BackendType Backend { get; init; }
    public required string ProviderProfileId { get; init; }
    public required string ModelId { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? SpeedMode { get; init; }
    public string? ExecutionMode { get; init; }
    public DataClassification ProjectDataClass { get; init; } = DataClassification.PrivateSource;
    public RoutingPolicy Policy { get; init; } = RoutingPolicy.Balanced;
    public string PolicySource { get; init; } = "GlobalDefault";
    public string? PinnedAccountId { get; init; }
    public SessionBinding? ExistingStickyBinding { get; init; }
    public bool OptInEstimatedQuota { get; init; } = false;

    /// <summary>
    /// Automatic policies skip a <see cref="HealthState.ForcedEnabled"/> account or model route
    /// unless the caller sets this. Pinned, ManualOnly, and an existing sticky binding are already
    /// an explicit selection. The flag does not bypass an account cooldown, quarantine, or auth block.
    /// </summary>
    public bool OptInForcedRoute { get; init; }
    public IReadOnlyDictionary<string, int>? ActiveExecutionsPerAccount { get; init; }
    public IReadOnlyDictionary<string, double>? LatencyEmaPerAccount { get; init; }
}
