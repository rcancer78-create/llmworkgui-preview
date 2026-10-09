namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public sealed record OpenCodeProviderInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? BaseUrl { get; init; }

    public bool IsConnected { get; init; }

    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
}
