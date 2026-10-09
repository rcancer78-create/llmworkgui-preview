using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Result of probing an account's authentication status (ТЗ §6.4).
/// </summary>
public sealed record AccountAuthProbeResult(
    AuthState State,
    string? ErrorMessage,
    DateTimeOffset ProbedAtUtc)
{
    public static AccountAuthProbeResult Valid(DateTimeOffset? probedAt = null) =>
        new(AuthState.Valid, null, probedAt ?? DateTimeOffset.UtcNow);

    public static AccountAuthProbeResult Invalid(string errorMessage, DateTimeOffset? probedAt = null) =>
        new(AuthState.Invalid, errorMessage, probedAt ?? DateTimeOffset.UtcNow);

    public static AccountAuthProbeResult Unknown(string? reason = null, DateTimeOffset? probedAt = null) =>
        new(AuthState.Unknown, reason, probedAt ?? DateTimeOffset.UtcNow);
}
