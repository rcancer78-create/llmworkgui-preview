namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Discovered Cursor ACP model descriptor with its per-model parameterized override definitions
/// (ADR-0003 §7.1). Contexts are recorded as discovered strings (for example <c>200k</c>).
/// </summary>
public sealed record CursorAcpModelInfo
{
    /// <summary>Base model id sent on the wire before any override is appended.</summary>
    public required string ModelId { get; init; }

    /// <summary>Discovered model family (for example <c>grok</c>, <c>claude</c>, <c>gpt</c>).</summary>
    public required string Family { get; init; }

    /// <summary>Human-readable model name for the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Default context window reported by discovery.</summary>
    public required string DefaultContext { get; init; }

    /// <summary>Maximum context window reported by discovery.</summary>
    public required string MaxContext { get; init; }

    /// <summary>Per-model override parameters; the only authority for override validation.</summary>
    public required IReadOnlyList<CursorAcpOverrideDefinition> Overrides { get; init; }
}
