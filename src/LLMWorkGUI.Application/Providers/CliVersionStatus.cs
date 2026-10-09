namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Typed outcome of comparing an observed CLI version with its declared tested range (ТЗ §9.4).
/// </summary>
public enum CliVersionStatus
{
    /// <summary>The observed version is inside the declared tested range.</summary>
    Supported,

    /// <summary>
    /// No tested range is declared, or the observed version is newer than the maximum tested one.
    /// The adapter must pass a capability probe before use; this is never a silent fallback.
    /// </summary>
    CapabilityProbeRequired,

    /// <summary>The observed version is older than the declared minimum and must not be used.</summary>
    OlderThanMinimum,

    /// <summary>The adapter declares an exact required version and the observed one differs.</summary>
    ExactVersionMismatch,

    /// <summary>The version output could not be parsed into a comparable version.</summary>
    Unparseable,

    /// <summary>No version output was observed at all (for example the CLI is absent).</summary>
    NotDetected,

    /// <summary>No requirement is declared for this CLI name.</summary>
    UnknownCli
}
