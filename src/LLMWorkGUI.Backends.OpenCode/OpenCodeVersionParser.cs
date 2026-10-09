using System.Text.RegularExpressions;
using LLMWorkGUI.Backends.Abstractions.OpenCode;

namespace LLMWorkGUI.Backends.OpenCode;

public static partial class OpenCodeVersionParser
{
    [GeneratedRegex(@"(?<version>\d+\.\d+(?:\.\d+){0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? output, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = VersionPattern().Match(output);

        if (!match.Success)
        {
            return false;
        }

        if (!Version.TryParse(match.Groups["version"].Value, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    public static bool IsSupported(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version >= OpenCodeVersionBaseline.MinimumSupportedVersion;
    }
}
