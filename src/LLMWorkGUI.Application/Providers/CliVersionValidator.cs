using System.Text.RegularExpressions;

namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Compares observed CLI versions with the declared tested ranges of the supported adapters. A version
/// outside the tested range never results in a silent fallback: it either blocks the adapter or
/// requires an explicit capability probe (ТЗ §9.4).
/// </summary>
public sealed partial class CliVersionValidator
{
    public const string OpenCodeCliName = "opencode";

    public const string CursorAgentCliName = "cursor-agent";

    public const string CodexCliName = "codex";

    public const string AgyCliName = "agy";

    public const string MirasimCliName = "mirasim";

    public static Version OpenCodeTestedVersion { get; } = new(1, 18, 31);

    public static Version CodexTestedVersion { get; } = new(0, 155, 0);

    public static Version AgyTestedVersion { get; } = new(1, 1, 23);

    public static Version MirasimSupportedVersion { get; } = new(0, 0, 354);

    /// <summary>
    /// Declared adapter matrix. OpenCode, Codex and AGY pin the observed baseline as both minimum and
    /// maximum tested version; Mirasim declares an exact version because its health endpoint does;
    /// Cursor Agent uses a date-based build number, so only a capability probe can judge it.
    /// </summary>
    public static IReadOnlyList<CliVersionRequirement> DefaultRequirements { get; } = new[]
    {
        new CliVersionRequirement(
            OpenCodeCliName,
            "OpenCode",
            OpenCodeTestedVersion,
            OpenCodeTestedVersion,
            requiredVersion: null,
            "ADR-0001 runtime/CLI baseline; OpenCodeVersionBaseline.MinimumSupportedVersion."),
        new CliVersionRequirement(
            CursorAgentCliName,
            "Cursor Agent",
            minimumVersion: null,
            maximumTestedVersion: null,
            requiredVersion: null,
            "ADR-0001 date-based Cursor Agent build; no semantic range is declared, capability probe required."),
        new CliVersionRequirement(
            CodexCliName,
            "Codex CLI",
            CodexTestedVersion,
            CodexTestedVersion,
            requiredVersion: null,
            "ADR-0001 CLI baseline observed 2026-09-22; integration runs only through star-cliproxy."),
        new CliVersionRequirement(
            AgyCliName,
            "AGY CLI",
            AgyTestedVersion,
            AgyTestedVersion,
            requiredVersion: null,
            "ADR-0001 CLI baseline observed 2026-09-22; account selection stays in agy-profile."),
        new CliVersionRequirement(
            MirasimCliName,
            "Mirasim",
            minimumVersion: null,
            maximumTestedVersion: null,
            requiredVersion: MirasimSupportedVersion,
            "MirasimHealthStatus.SupportedVersion (exact host contract).")
    };

    public CliVersionValidator(IEnumerable<CliVersionRequirement>? requirements = null)
    {
        Requirements = (requirements ?? DefaultRequirements).ToArray();

        if (Requirements.Count == 0)
        {
            throw new ArgumentException("At least one CLI requirement is required.", nameof(requirements));
        }
    }

    public IReadOnlyList<CliVersionRequirement> Requirements { get; }

    /// <summary>Validates the observed version output of one CLI.</summary>
    public CliVersionAssessment Validate(string cliName, string? versionOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);

        var requirement = Requirements.FirstOrDefault(candidate => string.Equals(
            candidate.CliName,
            cliName,
            StringComparison.OrdinalIgnoreCase));

        if (requirement is null)
        {
            return new CliVersionAssessment
            {
                CliName = cliName,
                DisplayName = cliName,
                ObservedVersion = null,
                Status = CliVersionStatus.UnknownCli,
                Requirement = null,
                Message = $"No version requirement is declared for '{cliName}'."
            };
        }

        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return CreateAssessment(
                requirement,
                null,
                CliVersionStatus.NotDetected,
                $"No version output was observed for '{requirement.DisplayName}'.");
        }

        if (!TryParseVersion(versionOutput, out var observed) || observed is null ||
            !HasExpectedLabel(versionOutput, requirement))
        {
            return CreateAssessment(
                requirement,
                versionOutput.Trim(),
                CliVersionStatus.Unparseable,
                $"The version output of '{requirement.DisplayName}' could not be parsed.");
        }

        var normalizedObserved = CliVersionRequirement.Normalize(observed);

        if (requirement.RequiredVersion is { } required)
        {
            return normalizedObserved.Equals(required)
                ? CreateAssessment(
                    requirement,
                    normalizedObserved.ToString(),
                    CliVersionStatus.Supported,
                    $"'{requirement.DisplayName}' matches the required version {required}.")
                : CreateAssessment(
                    requirement,
                    normalizedObserved.ToString(),
                    CliVersionStatus.ExactVersionMismatch,
                    $"'{requirement.DisplayName}' is {normalizedObserved}, but {required} is required; "
                    + "no silent fallback is permitted.");
        }

        if (requirement.MinimumVersion is { } minimum && normalizedObserved < minimum)
        {
            return CreateAssessment(
                requirement,
                normalizedObserved.ToString(),
                CliVersionStatus.OlderThanMinimum,
                $"'{requirement.DisplayName}' is {normalizedObserved}, below the minimum {minimum}; "
                + "the adapter must not be used.");
        }

        if (requirement.MaximumTestedVersion is { } maximum && normalizedObserved > maximum)
        {
            return CreateAssessment(
                requirement,
                normalizedObserved.ToString(),
                CliVersionStatus.CapabilityProbeRequired,
                $"'{requirement.DisplayName}' is {normalizedObserved}, newer than the maximum tested {maximum}; "
                + "a capability probe is required before use.");
        }

        if (!requirement.DeclaresVersionRange)
        {
            return CreateAssessment(
                requirement,
                normalizedObserved.ToString(),
                CliVersionStatus.CapabilityProbeRequired,
                $"'{requirement.DisplayName}' has no declared tested range; a capability probe is required.");
        }

        return CreateAssessment(
            requirement,
            normalizedObserved.ToString(),
            CliVersionStatus.Supported,
            $"'{requirement.DisplayName}' {normalizedObserved} is inside the declared tested range.");
    }

    /// <summary>
    /// Validates every declared requirement against the observed version outputs. Missing entries are
    /// reported as <see cref="CliVersionStatus.NotDetected"/> instead of being silently skipped.
    /// </summary>
    public IReadOnlyList<CliVersionAssessment> ValidateAll(IReadOnlyDictionary<string, string?> observedVersions)
    {
        ArgumentNullException.ThrowIfNull(observedVersions);

        var assessments = new List<CliVersionAssessment>(Requirements.Count);

        foreach (var requirement in Requirements)
        {
            var observed = Lookup(observedVersions, requirement.CliName);

            assessments.Add(Validate(requirement.CliName, observed));
        }

        return assessments;
    }

    public static bool TryParseVersion(string? output, out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var matches = VersionTokenRegex().Matches(output);

        if (matches.Count != 1)
        {
            return false;
        }

        var match = matches[0];

        var components = match.Groups["version"].Value.Split('.');
        var normalized = string.Join(
            '.',
            components.Select(component =>
            {
                var trimmed = component.TrimStart('0');
                return trimmed.Length == 0 ? "0" : trimmed;
            }));

        return Version.TryParse(normalized, out version) && version is not null;
    }

    private static bool HasExpectedLabel(string output, CliVersionRequirement requirement)
    {
        var match = VersionTokenRegex().Match(output);
        var prefix = output[..match.Index].Trim();
        if (prefix.EndsWith("v", StringComparison.OrdinalIgnoreCase))
            prefix = prefix[..^1].TrimEnd();
        if (prefix.EndsWith("version", StringComparison.OrdinalIgnoreCase))
            prefix = prefix[..^7].TrimEnd();
        return prefix.Length == 0 ||
            string.Equals(prefix, requirement.CliName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(prefix, requirement.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            (requirement.CliName == CodexCliName && string.Equals(prefix, "codex-cli", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Lookup(IReadOnlyDictionary<string, string?> observedVersions, string cliName)
    {
        foreach (var pair in observedVersions)
        {
            if (string.Equals(pair.Key, cliName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static CliVersionAssessment CreateAssessment(
        CliVersionRequirement requirement,
        string? observedVersion,
        CliVersionStatus status,
        string message)
    {
        return new CliVersionAssessment
        {
            CliName = requirement.CliName,
            DisplayName = requirement.DisplayName,
            ObservedVersion = observedVersion,
            Status = status,
            Requirement = requirement,
            Message = message
        };
    }

    [GeneratedRegex(@"(?<![\d.])(?<version>\d+\.\d+(?:\.\d+){0,2})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();
}
