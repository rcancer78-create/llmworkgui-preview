namespace LLMWorkGUI.Domain.Entities;

/// <summary>A persisted header contains either public text or a secret reference, never both.</summary>
public sealed class ProviderHeader
{
    public ProviderHeader(string name, string? value = null, string? secretReference = null)
    {
        Name = DomainGuard.NotBlank(name, nameof(name));
        if (Name.Length > 256 || Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Invalid header name.", nameof(name));
        if (value?.Length > 32768 || value?.Any(c => char.IsControl(c) && c != '\t') == true)
            throw new ArgumentException("Invalid header value.", nameof(value));
        if (secretReference is not null && value is not null)
            throw new ArgumentException("A secret header cannot contain public text.");
        Value = secretReference is null ? value ?? string.Empty : null;
        SecretReference = DomainGuard.OptionalNotBlank(secretReference, nameof(secretReference));
    }

    public string Name { get; }
    public string? Value { get; }
    public string? SecretReference { get; }
}
