namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Safety and availability policy constants for the star-cliproxy boundary (ADR-0007, ТЗ §6.4, §6.11a).
/// </summary>
public static class StarCliProxyPolicy
{
    /// <summary>Canonical source of the star-cliproxy gateway used for Codex/AGY routing.</summary>
    public const string ProjectUrl = "https://github.com/starhunt/star-cliproxy";

    /// <summary>
    /// Exact degraded-mode blocker reported when the star-cliproxy gateway cannot be located in
    /// PATH or the environment. No OpenCode or direct-CLI fallback is permitted.
    /// </summary>
    public const string NotInstalledBlocker =
        "The star-cliproxy gateway was not found on PATH or in the environment " +
        "(configured executable path, STAR_CLIPROXY_EXECUTABLE or STAR_CLIPROXY_PATH). " +
        "Install or build it from https://github.com/starhunt/star-cliproxy and retry. " +
        "Codex/AGY routes stay in degraded mode (no OpenCode fallback) until the gateway is available.";

    /// <summary>
    /// Reason reported when a Codex account has no registered absolute CODEX_HOME context.
    /// </summary>
    public const string CodexHomeRequiredReason =
        "No Codex account context is registered for this account. Every Codex account requires its own " +
        "absolute CODEX_HOME directory that the user creates and authenticates independently " +
        "(ТЗ §6.4, §6.11a); global HOME/USERPROFILE are never modified and auth.json is never copied.";

    /// <summary>Account id prefixes used by the star-cliproxy bridge discovery.</summary>
    public const string CodexAccountIdPrefix = "codex:";

    public const string AgyAccountIdPrefix = "agy:";
}
