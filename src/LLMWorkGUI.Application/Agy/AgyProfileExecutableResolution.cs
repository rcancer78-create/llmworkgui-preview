namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Result of locating the agy-profile utility entry point (ТЗ §6.11a).
/// </summary>
public sealed record AgyProfileExecutableResolution
{
    private AgyProfileExecutableResolution(string? executablePath)
    {
        ExecutablePath = executablePath;
    }

    public string? ExecutablePath { get; }

    public bool IsAvailable => ExecutablePath is not null;

    public static AgyProfileExecutableResolution Found(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return new AgyProfileExecutableResolution(executablePath);
    }

    public static AgyProfileExecutableResolution NotFound() => new(executablePath: null);
}
