namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Result of selecting and verifying one account context. When <see cref="IsResolved"/> is false
/// the context must not be launched; the failure reason always identifies the blocker.
/// </summary>
public sealed record AccountContextSelection(
    bool IsResolved,
    AccountContextKind Kind,
    string AccountId,
    string? CodexHomePath = null,
    string? AgyProfileName = null,
    bool RequiresNewSession = false,
    string? FailureReason = null)
{
    public static AccountContextSelection ResolvedCodex(CodexAccountContext context, bool requiresNewSession = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AccountContextSelection(
            IsResolved: true,
            AccountContextKind.Codex,
            context.AccountId,
            CodexHomePath: context.CodexHomePath,
            RequiresNewSession: requiresNewSession);
    }

    public static AccountContextSelection ResolvedAgy(string profileName, bool requiresNewSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        return new AccountContextSelection(
            IsResolved: true,
            AccountContextKind.Agy,
            profileName,
            AgyProfileName: profileName,
            RequiresNewSession: requiresNewSession);
    }

    public static AccountContextSelection Unresolved(
        AccountContextKind kind,
        string accountId,
        string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        return new AccountContextSelection(
            IsResolved: false,
            kind,
            accountId,
            FailureReason: failureReason);
    }
}
