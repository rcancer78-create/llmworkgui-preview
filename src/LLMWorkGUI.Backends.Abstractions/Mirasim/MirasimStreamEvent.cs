namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed record MirasimStreamEvent
{
    public required string TurnId { get; init; }

    public required string EventType { get; init; }

    public string? DeltaText { get; init; }

    public bool IsTerminal { get; init; }

    public string? RawPayload { get; init; }
}
