namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Safety policy constants for the native AGY integration (ТЗ §6.4, §6.11a).
/// </summary>
public static class AgyProfilePolicy
{
    /// <summary>Canonical installation source of the user-selected agy-profile utility.</summary>
    public const string InstallUrl = "https://github.com/haclongkim/agy-profile";

    /// <summary>
    /// Exact availability blocker reported when the agy-profile utility cannot be located
    /// on PATH or in <c>%LOCALAPPDATA%\agy-profile</c>.
    /// </summary>
    public const string NotInstalledBlocker =
        "agy-profile utility was not found on PATH or at %LOCALAPPDATA%\\agy-profile\\agy-profile.cmd / " +
        "agy-profile.ps1. Install it from https://github.com/haclongkim/agy-profile and retry. " +
        "Native AGY multi-account switching stays disabled until the utility is available.";

    /// <summary>Automatic rotation commands that must never be issued by the application.</summary>
    public static readonly IReadOnlyList<string> ForbiddenRotationCommands =
        Array.AsReadOnly(new[] { "next", "random" });

    /// <summary>Forced switch flag that the application must never pass (ТЗ §6.11a).</summary>
    public const string ForcedSwitchFlag = "-Force";

    /// <summary>
    /// True when a profile name may be passed to <c>agy-profile</c>: only ASCII letters, digits,
    /// '-' and '_' are accepted so no switch can smuggle command/rotation syntax.
    /// </summary>
    public static bool IsValidProfileName(string? profileName) =>
        !string.IsNullOrWhiteSpace(profileName) &&
        !profileName.StartsWith('-') &&
        profileName.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
