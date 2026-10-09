using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>Redacts diagnostic text while preserving the quota scheduler's short-value rules.</summary>
public static class QuotaDiagnosticRedactor
{
    private static readonly CredentialTextRedactor Redactor = new();
    private static readonly Regex LegacyCredentialRegex = new(
        @"(bearer\s+|api[_-]?key[:=]\s*|token[:=]\s*|password[:=]\s*)([^\s;,]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Apply legacy rules to decoded string values before writing JSON. Applying them to
    // serialized JSON can consume closing quotes and corrupt escape sequences.
    public static string Redact(string text) => Redactor.RedactJson(text, preserveCleanFormatting: true,
        additionalTextRedactor: MaskLegacyCredentials, replacement: "***REDACTED***");

    private static string MaskLegacyCredentials(string text) => LegacyCredentialRegex.Replace(text, static match =>
        match.Groups[2].Value is CredentialTextRedactor.Placeholder or "***REDACTED***"
            ? match.Value : match.Groups[1].Value + "***REDACTED***");
}
