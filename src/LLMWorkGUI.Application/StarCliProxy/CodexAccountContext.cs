namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Immutable Codex account context: one account is one pre-created absolute CODEX_HOME directory.
/// The directory is authenticated by the user independently; the application never copies
/// <c>auth.json</c> between directories and never changes global HOME/USERPROFILE (ADR-0007 §4).
/// </summary>
public sealed record CodexAccountContext
{
    public CodexAccountContext(string accountId, string codexHomePath, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        if (!Path.IsPathFullyQualified(codexHomePath))
        {
            throw new ArgumentException(
                $"The CODEX_HOME path '{codexHomePath}' must be absolute; each Codex account uses a separate " +
                "absolute directory (ТЗ §6.4, §6.11a).",
                nameof(codexHomePath));
        }

        AccountId = accountId;
        CodexHomePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(codexHomePath));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? accountId : displayName;
    }

    public string AccountId { get; }

    /// <summary>Absolute, normalized CODEX_HOME path for this account.</summary>
    public string CodexHomePath { get; }

    public string DisplayName { get; }
}
