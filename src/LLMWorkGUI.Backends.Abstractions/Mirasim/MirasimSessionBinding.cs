using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed record MirasimSessionBinding
{
    /// <summary>Local submitted binding, not native-origin evidence.</summary>
    public ProjectProviderContext? ProjectContext { get; init; }
    public required string InstanceId { get; init; }

    public required string Harness { get; init; }

    public required string ModelId { get; init; }

    public required string RouteMode { get; init; }

    public required string SessionKey { get; init; }

    public required string WorkspacePath { get; init; }
}
