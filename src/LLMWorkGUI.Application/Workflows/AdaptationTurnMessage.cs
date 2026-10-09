namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// A single message of the adaptation conversation. Roles are limited to the two wire values the
/// adaptation prompt contract accepts: "user" and "assistant".
/// </summary>
public sealed record AdaptationTurnMessage
{
    public AdaptationTurnMessage(string role, string content)
    {
        Role = ApplicationGuard.NotBlank(role, nameof(role));

        if (!string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Adaptation turn message role must be 'user' or 'assistant'.",
                nameof(role));
        }

        Role = Role.ToLowerInvariant();
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public string Role { get; }

    public string Content { get; }
}
