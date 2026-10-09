using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Result of an account pinning attempt (ТЗ §6.4, ADR-0004).
/// </summary>
public sealed record AccountPinResult(
    bool IsPinned,
    string? FailureReason = null,
    SessionBinding? ConfirmedBinding = null,
    bool RequiresNewSession = false)
{
    public static AccountPinResult Success(SessionBinding binding, bool requiresNewSession = false) =>
        new(true, null, binding, requiresNewSession);

    public static AccountPinResult Failure(string reason) => new(false, reason, null);
}
