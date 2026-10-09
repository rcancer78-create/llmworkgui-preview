namespace LLMWorkGUI.Application.Security;

public static class SecretReference
{
    public const string Prefix = "urn:llmworkgui:secret:";

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = value.AsSpan(Prefix.Length);

        if (identifier.IsEmpty)
        {
            return false;
        }

        foreach (var character in identifier)
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_'
                or '-';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    public static string Create(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        var reference = Prefix + identifier;

        if (!IsValid(reference))
        {
            throw new ArgumentException(
                "Secret reference identifiers may only contain lowercase letters, digits, '_' and '-'.",
                nameof(identifier));
        }

        return reference;
    }

    public static string GetIdentifier(string secretReference)
    {
        if (!IsValid(secretReference))
        {
            throw new ArgumentException(
                $"Secret reference must match the canonical form '{Prefix}<identifier>'.",
                nameof(secretReference));
        }

        return secretReference[Prefix.Length..];
    }
}
