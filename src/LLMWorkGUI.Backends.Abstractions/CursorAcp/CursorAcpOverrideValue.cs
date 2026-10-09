namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>A single parameter override pair (<c>name=value</c>) of a model selection.</summary>
public sealed record CursorAcpOverrideValue
{
    /// <summary>Override parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Override parameter value.</summary>
    public required string Value { get; init; }
}
