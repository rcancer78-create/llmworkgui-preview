using System.Collections.Frozen;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public sealed record OpenCodeConfiguredProvidersResponse
{
    public static readonly IReadOnlyDictionary<string, string> EmptyDefaultModels =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public IReadOnlyList<OpenCodeProviderInfo> Providers { get; init; } = Array.Empty<OpenCodeProviderInfo>();

    public IReadOnlyDictionary<string, string> DefaultModels { get; init; } = EmptyDefaultModels;
}
