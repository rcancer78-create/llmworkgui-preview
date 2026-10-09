using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Request for resolving one immutable account context behind the star-cliproxy boundary.
/// </summary>
public sealed record AccountContextRequest(BackendType Backend, AccountContextKind Kind, string AccountId)
{
    public static AccountContextRequest ForCodex(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return new AccountContextRequest(BackendType.StarCliProxy, AccountContextKind.Codex, accountId);
    }

    public static AccountContextRequest ForAgy(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        return new AccountContextRequest(BackendType.StarCliProxy, AccountContextKind.Agy, profileName);
    }
}
