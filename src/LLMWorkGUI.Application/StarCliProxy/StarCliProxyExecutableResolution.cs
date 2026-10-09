namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Result of locating the managed star-cliproxy gateway entry point (ADR-0007, ТЗ §6.11a).
/// </summary>
public sealed record StarCliProxyExecutableResolution
{
    private StarCliProxyExecutableResolution(string? executablePath, string? blocker)
    {
        ExecutablePath = executablePath;
        Blocker = blocker;
    }

    public string? ExecutablePath { get; }

    public string? Blocker { get; }

    public bool IsAvailable => ExecutablePath is not null;

    public static StarCliProxyExecutableResolution Found(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return new StarCliProxyExecutableResolution(executablePath, null);
    }

    public static StarCliProxyExecutableResolution NotFound(string? blocker = null) =>
        new(null, blocker ?? StarCliProxyPolicy.NotInstalledBlocker);
}
