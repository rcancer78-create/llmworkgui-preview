using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>Infrastructure compatibility facade over the shared credential redactor.</summary>
public sealed class SensitiveDataFilter
{
    public const string Placeholder = CredentialTextRedactor.Placeholder;
    private readonly CredentialTextRedactor _redactor = new();

    public string Redact(string? input) => _redactor.Redact(input);
    public string RedactDiagnostic(string? input) => _redactor.RedactDiagnostic(input);
    public string RedactJson(string? json) => _redactor.RedactJson(json);
    public bool ContainsSensitiveData(string? input) => _redactor.ContainsSensitiveData(input);
    public static bool IsSensitiveName(string name) => CredentialTextRedactor.IsSensitiveName(name);
}
