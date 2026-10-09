namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Availability state of the Cursor Agent CLI discovered on the current Windows user account.</summary>
public enum CursorAcpExecutableStatus
{
    /// <summary>The executable was located and its version was probed successfully.</summary>
    Found,

    /// <summary>The executable was not found on PATH, in the environment override, or in standard install locations.</summary>
    Missing,

    /// <summary>The executable was located but its version could not be probed (timeout, non-zero exit, unparsable output).</summary>
    Unresolved
}

/// <summary>
/// Result of locating and probing the Cursor Agent CLI (ADR-0003 §2, ТЗ §6.2). Missing or
/// unresolved executables produce a typed degraded resolution instead of an exception.
/// </summary>
public sealed record CursorAcpExecutableResolution
{
    private CursorAcpExecutableResolution(
        CursorAcpExecutableStatus status,
        string? executablePath,
        string? version,
        string? blocker,
        string? guidance)
    {
        Status = status;
        ExecutablePath = executablePath;
        Version = version;
        Blocker = blocker;
        Guidance = guidance;
    }

    public CursorAcpExecutableStatus Status { get; }

    public string? ExecutablePath { get; }

    public string? Version { get; }

    /// <summary>Human-readable reason of the degraded state; null when the executable is available.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the executable is available.</summary>
    public string? Guidance { get; }

    public bool IsAvailable => Status == CursorAcpExecutableStatus.Found;

    public bool IsDegraded => !IsAvailable;

    public static CursorAcpExecutableResolution Found(string executablePath, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return new CursorAcpExecutableResolution(
            CursorAcpExecutableStatus.Found,
            executablePath,
            version,
            blocker: null,
            guidance: null);
    }

    public static CursorAcpExecutableResolution Missing(
        string? blocker = null,
        string? guidance = null) =>
        new(
            CursorAcpExecutableStatus.Missing,
            executablePath: null,
            version: null,
            blocker ?? CursorAcpPolicy.NotInstalledBlocker,
            guidance ?? CursorAcpPolicy.NotInstalledGuidance);

    public static CursorAcpExecutableResolution Unresolved(
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpExecutableResolution(
            CursorAcpExecutableStatus.Unresolved,
            executablePath: null,
            version: null,
            blocker,
            guidance ?? CursorAcpPolicy.VersionProbeGuidance);
    }
}
