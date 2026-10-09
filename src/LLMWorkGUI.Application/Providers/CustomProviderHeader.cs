using System.Text.RegularExpressions;

namespace LLMWorkGUI.Application.Providers;

public sealed class CustomProviderHeader
{
    private static readonly Regex ValidHeaderNameRegex = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public CustomProviderHeader(string name, string value, bool isSecret = false, string? secretReference = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Header name cannot be empty.", nameof(name));
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > 256 || !ValidHeaderNameRegex.IsMatch(trimmedName))
        {
            throw new ArgumentException($"Header name '{trimmedName}' contains invalid characters.", nameof(name));
        }

        if (value is not null && (value.Length > 32768 || value.Any(character => char.IsControl(character) && character != '\t')))
        {
            throw new ArgumentException("Header value contains invalid control characters.", nameof(value));
        }

        Name = trimmedName;
        Value = value ?? string.Empty;
        if (secretReference is not null && (!Security.SecretReference.IsValid(secretReference) || Value.Length != 0))
            throw new ArgumentException("A stored secret header requires a canonical reference and no plaintext value.");
        SecretReference = secretReference;
        IsSecret = isSecret || secretReference is not null || Security.CredentialTextRedactor.IsSensitiveHeaderName(Name);
    }

    public string Name { get; }

    public string Value { get; }

    public bool IsSecret { get; }
    public string? SecretReference { get; }
}
