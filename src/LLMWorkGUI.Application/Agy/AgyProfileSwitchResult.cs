namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Result of an explicit <c>agy-profile switch &lt;name&gt;</c> attempt (ТЗ §6.11a).
/// A successful switch is only reported after the active profile was re-read and
/// matched the requested profile.
/// </summary>
public sealed record AgyProfileSwitchResult(
    bool IsSwitched,
    string? ActiveProfile,
    string? FailureReason = null)
{
    public static AgyProfileSwitchResult Success(string activeProfile) =>
        new(true, activeProfile, null);

    public static AgyProfileSwitchResult Failure(string reason) =>
        new(false, null, reason);
}
