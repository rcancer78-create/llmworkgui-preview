using LLMWorkGUI.Application.Agy;

namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Immutable AGY account context: one saved agy-profile profile. Profile selection happens only
/// through the documented <c>list/current/switch</c> commands under the shared account lock and
/// never while an agy process is running (ТЗ §6.11a, ADR-0007 §3).
/// </summary>
public sealed record AgyAccountContext
{
    public AgyAccountContext(string profileName)
    {
        if (!AgyProfilePolicy.IsValidProfileName(profileName))
        {
            throw new ArgumentException(
                $"AGY profile name '{profileName}' is invalid: only letters, digits, '-' and '_' are allowed.",
                nameof(profileName));
        }

        ProfileName = profileName;
    }

    public string ProfileName { get; }

    public string AccountId => ProfileName;
}
