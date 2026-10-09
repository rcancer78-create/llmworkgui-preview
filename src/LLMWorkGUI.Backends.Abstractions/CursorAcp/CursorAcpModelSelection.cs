namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Validated base model id plus its override set. The base id and the override set are stored
/// separately (ADR-0003 §7.2); <see cref="ICursorAcpModelSelector"/> combines them into the wire
/// form <c>&lt;baseModelId&gt;[&lt;parameter&gt;=&lt;value&gt;,...]</c>.
/// </summary>
public sealed record CursorAcpModelSelection
{
    /// <summary>Base model id without overrides.</summary>
    public required string BaseModelId { get; init; }

    /// <summary>Validated override pairs; empty when no parameter is overridden.</summary>
    public required IReadOnlyList<CursorAcpOverrideValue> Overrides { get; init; }
}
