using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Per-model override parameter confirmed by discovery. Only parameters whose
/// <see cref="CapabilityState"/> is <see cref="CapabilityState.Supported"/> may be selected or
/// sent on the wire (ADR-0003 §7.3).
/// </summary>
public sealed record CursorAcpOverrideDefinition
{
    /// <summary>Override parameter name (for example <c>context</c>, <c>effort</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Values confirmed for this parameter by per-model discovery.</summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>Default value reported by discovery; always one of <see cref="Values"/>.</summary>
    public required string Default { get; init; }

    /// <summary>Discovered capability state of the parameter.</summary>
    public required CapabilityState CapabilityState { get; init; }
}
