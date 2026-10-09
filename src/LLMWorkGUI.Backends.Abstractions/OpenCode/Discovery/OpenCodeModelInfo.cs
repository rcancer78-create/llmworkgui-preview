namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public sealed record OpenCodeModelInfo
{
    public required string Id { get; init; }

    public required string ProviderId { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<string> Variants { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public int? ContextLimit { get; init; }
}
