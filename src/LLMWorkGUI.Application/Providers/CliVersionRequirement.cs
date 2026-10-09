namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Declared compatibility range of one external CLI. A requirement with no version bound always
/// demands a capability probe, which is how adapters stay honest about unverified versions
/// (ТЗ §9.4).
/// </summary>
public sealed record CliVersionRequirement
{
    public CliVersionRequirement(
        string cliName,
        string displayName,
        Version? minimumVersion = null,
        Version? maximumTestedVersion = null,
        Version? requiredVersion = null,
        string provenance = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (minimumVersion is not null
            && maximumTestedVersion is not null
            && Normalize(maximumTestedVersion) < Normalize(minimumVersion))
        {
            throw new ArgumentException(
                "The maximum tested version must not be lower than the minimum version.",
                nameof(maximumTestedVersion));
        }

        if (requiredVersion is not null && (minimumVersion is not null || maximumTestedVersion is not null))
        {
            throw new ArgumentException(
                "An exact required version cannot be combined with a minimum/maximum range.",
                nameof(requiredVersion));
        }

        CliName = cliName;
        DisplayName = displayName;
        MinimumVersion = minimumVersion is null ? null : Normalize(minimumVersion);
        MaximumTestedVersion = maximumTestedVersion is null ? null : Normalize(maximumTestedVersion);
        RequiredVersion = requiredVersion is null ? null : Normalize(requiredVersion);
        Provenance = provenance;
    }

    public string CliName { get; }

    public string DisplayName { get; }

    public Version? MinimumVersion { get; }

    public Version? MaximumTestedVersion { get; }

    public Version? RequiredVersion { get; }

    /// <summary>Where the declared range comes from (ADR or capability snapshot).</summary>
    public string Provenance { get; }

    public bool DeclaresVersionRange =>
        MinimumVersion is not null || MaximumTestedVersion is not null || RequiredVersion is not null;

    internal static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }
}
