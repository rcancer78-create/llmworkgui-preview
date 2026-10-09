namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Describes one managed star-cliproxy launch. The account context is carried as an environment
/// overlay for the owned process only; global HOME/USERPROFILE are never modified (ADR-0007 §4).
/// </summary>
public sealed record StarCliProxyServerStartRequest
{
    public static readonly IReadOnlyList<string> DefaultProviderIds = ["codex", "agy"];

    public required string ExecutionId { get; init; }

    /// <summary>Absolute CODEX_HOME for the Codex account context, or null for an AGY/default context.</summary>
    public string? CodexHomePath { get; init; }

    /// <summary>Provider ids enabled in the generated gateway configuration.</summary>
    public IReadOnlyList<string> EnabledProviderIds { get; init; } = DefaultProviderIds;
}
