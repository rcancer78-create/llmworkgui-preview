namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class OpenCodeStreamOptions
{
    public const string SectionName = "OpenCodeStream";

    public const int DefaultMaxInMemoryEvents = 1000;

    public const string DefaultSpoolFileName = "opencode-events.jsonl";

    public int MaxInMemoryEvents { get; set; } = DefaultMaxInMemoryEvents;

    public string? SpoolDirectory { get; set; }

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxInMemoryEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInMemoryEvents),
                MaxInMemoryEvents,
                "MaxInMemoryEvents must be positive.");
        }

        if (SpoolDirectory is not null && string.IsNullOrWhiteSpace(SpoolDirectory))
        {
            throw new ArgumentException(
                "SpoolDirectory must not be empty or whitespace when specified.",
                nameof(SpoolDirectory));
        }

        if (ReconnectInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReconnectInitialDelay),
                ReconnectInitialDelay,
                "ReconnectInitialDelay must be positive.");
        }

        if (ReconnectMaxDelay < ReconnectInitialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReconnectMaxDelay),
                ReconnectMaxDelay,
                "ReconnectMaxDelay must not be smaller than ReconnectInitialDelay.");
        }
    }
}
