namespace LLMWorkGUI.Backends.OpenCode.Discovery;

public sealed class OpenCodeCapabilityCacheOptions
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    public TimeSpan Ttl { get; set; } = DefaultTtl;

    public void Validate()
    {
        if (Ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Ttl),
                Ttl,
                "The capability cache TTL must be positive.");
        }
    }
}
