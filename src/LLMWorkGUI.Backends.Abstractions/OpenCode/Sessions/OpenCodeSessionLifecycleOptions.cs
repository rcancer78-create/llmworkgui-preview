namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed class OpenCodeSessionLifecycleOptions
{
    public const string SectionName = "OpenCodeSessionLifecycle";

    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan CancellationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConnectionTimeout), ConnectionTimeout, "ConnectionTimeout must be positive.");
        }

        if (TurnTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TurnTimeout),
                TurnTimeout,
                "TurnTimeout must be positive.");
        }

        if (CancellationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CancellationTimeout),
                CancellationTimeout,
                "CancellationTimeout must be positive.");
        }
    }
}
