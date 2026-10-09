namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Result of validating one CLI version against its declared requirement. The typed status is the
/// only input callers need: a version mismatch can never silently fall back (ТЗ §9.4).
/// </summary>
public sealed record CliVersionAssessment
{
    public required string CliName { get; init; }

    public required string DisplayName { get; init; }

    public required string? ObservedVersion { get; init; }

    public required CliVersionStatus Status { get; init; }

    public required CliVersionRequirement? Requirement { get; init; }

    public required string Message { get; init; }

    public bool IsSupported => Status == CliVersionStatus.Supported;

    public bool RequiresCapabilityProbe => Status == CliVersionStatus.CapabilityProbeRequired;

    /// <summary>
    /// True when the observed version must not be used at all; false for a supported version, an
    /// absent CLI and a newer-than-tested version that is allowed only after a capability probe.
    /// </summary>
    public bool BlocksSilentFallback => Status is
        CliVersionStatus.OlderThanMinimum
        or CliVersionStatus.ExactVersionMismatch
        or CliVersionStatus.Unparseable
        or CliVersionStatus.UnknownCli;
}
