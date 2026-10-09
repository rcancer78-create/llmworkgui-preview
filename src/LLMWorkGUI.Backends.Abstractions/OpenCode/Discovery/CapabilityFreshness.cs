namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public sealed record CapabilityFreshness
{
    public DateTime LastRefreshedAtUtc { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    public bool IsFresh { get; init; }
}
