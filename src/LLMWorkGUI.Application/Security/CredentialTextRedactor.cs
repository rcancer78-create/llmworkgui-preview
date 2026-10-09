using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMWorkGUI.Application.Security;

public sealed partial class CredentialTextRedactor
{
    public const string Placeholder = "[REDACTED]";

    private static readonly string[] SensitiveNameMarkers =
    [
        "apikey",
        "api_key",
        "api-key",
        "secret",
        "password",
        "passwd",
        "authorization",
        "cookie",
        "credential",
        "private_key",
        "privatekey",
        "access_key",
        "accesskey",
        "accesstoken",
        "refreshtoken",
        "idtoken",
        "sessiontoken"
    ];

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = PrivateKeyBlockRegex().Replace(input, Placeholder);
        result = AuthorizationLineRegex().Replace(result, AuthorizationLineReplacement);
        result = JsonStringValueRegex().Replace(result, JsonStringValueReplacement);
        result = BearerTokenRegex().Replace(result, BearerTokenReplacement);
        result = OpenAiStyleKeyRegex().Replace(result, Placeholder);
        result = GitHubTokenRegex().Replace(result, Placeholder);
        result = AwsAccessKeyRegex().Replace(result, Placeholder);
        result = SlackTokenRegex().Replace(result, Placeholder);
        result = CursorKeyRegex().Replace(result, Placeholder);
        result = GoogleApiKeyRegex().Replace(result, Placeholder);
        result = JwtRegex().Replace(result, Placeholder);
        result = UrlUserInfoRegex().Replace(result, "${scheme}" + Placeholder + "@");
        result = RedactUnquotedAssignments(result);
        result = AssignmentRegex().Replace(result, AssignmentReplacement);

        return result;
    }

    /// <summary>
    /// Redacts a complete JSON object or array using decoded property names and typed values.
    /// Other diagnostic text keeps the existing pattern-based policy. Clean JSON retains its bytes.
    /// </summary>
    public string RedactDiagnostic(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var trimmed = input.AsSpan().TrimStart();
        return !trimmed.IsEmpty && trimmed[0] is '{' or '['
            ? RedactJson(input, preserveCleanFormatting: true)
            : Redact(input);
    }

    public string RedactJson(string? json, bool preserveCleanFormatting = false,
        Func<string, string>? additionalTextRedactor = null, string replacement = Placeholder)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (string.IsNullOrWhiteSpace(json)) { return json ?? string.Empty; }

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return RedactText(json, additionalTextRedactor, replacement); }

        using (document)
        {
            // Preserve the infrastructure facade's existing scalar-string result by default.
            if (!preserveCleanFormatting && additionalTextRedactor is null
                && document.RootElement.ValueKind == JsonValueKind.String)
            {
                return Redact(document.RootElement.GetString());
            }
            if (!preserveCleanFormatting && additionalTextRedactor is null
                && document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return json;
            }
            if (document.RootElement.ValueKind == JsonValueKind.Null) { return json; }

            using var buffer = new MemoryStream();
            var changed = false;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedactedJson(document.RootElement, writer, additionalTextRedactor, replacement, ref changed);
            }
            return preserveCleanFormatting && !changed ? json : Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
    public bool ContainsSensitiveData(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        return !string.Equals(RedactDiagnostic(input), input, StringComparison.Ordinal);
    }

    /// <summary>HTTP header policy shared by persistence, editors and diagnostic export.</summary>
    public static bool IsSensitiveHeaderName(string name) => IsSensitiveName(name)
        || new[] { "key", "token", "auth", "pass", "pwd", "signature", "cert", "private" }
            .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var normalized = name.ToLowerInvariant();

        foreach (var marker in SensitiveNameMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return ContainsTokenWord(normalized);
    }

    private static bool ContainsTokenWord(string normalizedName)
    {
        var index = normalizedName.IndexOf("token", StringComparison.Ordinal);

        while (index >= 0)
        {
            var end = index + 5;
            var startsWord = index == 0 || !char.IsAsciiLetterLower(normalizedName[index - 1]);
            var endsWord = end >= normalizedName.Length || !char.IsAsciiLetterLower(normalizedName[end]);

            if (startsWord && endsWord)
            {
                return true;
            }

            index = normalizedName.IndexOf("token", end, StringComparison.Ordinal);
        }

        return false;
    }

    private void WriteRedactedJson(JsonElement element, Utf8JsonWriter writer,
        Func<string, string>? additionalTextRedactor, string replacement, ref bool changed)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        var original = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        var masked = original == Placeholder || original == replacement ? original : replacement;
                        writer.WriteStringValue(masked);
                        changed |= !string.Equals(original, masked, StringComparison.Ordinal);
                    }
                    else
                    {
                        WriteRedactedJson(property.Value, writer, additionalTextRedactor, replacement, ref changed);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(item, writer, additionalTextRedactor, replacement, ref changed);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = element.GetString()!;
                var cleaned = RedactText(text, additionalTextRedactor, replacement);
                changed |= !string.Equals(text, cleaned, StringComparison.Ordinal);
                writer.WriteStringValue(cleaned);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private string RedactText(string text, Func<string, string>? additionalTextRedactor, string replacement)
    {
        var cleaned = Redact(text);
        if (!string.Equals(text, cleaned, StringComparison.Ordinal) && replacement != Placeholder)
        {
            cleaned = cleaned.Replace(Placeholder, replacement, StringComparison.Ordinal);
        }
        return additionalTextRedactor is null ? cleaned : additionalTextRedactor(cleaned);
    }
    private static string AuthorizationLineReplacement(Match match)
    {
        return match.Groups["name"].Value + match.Groups["sep"].Value + Placeholder;
    }

    private static string JsonStringValueReplacement(Match match)
    {
        if (!IsSensitiveName(match.Groups["name"].Value))
        {
            return match.Value;
        }

        return "\"" + match.Groups["name"].Value + "\"" + match.Groups["sep"].Value + "\"" + Placeholder + "\"";
    }

    private static string BearerTokenReplacement(Match match)
    {
        return match.Groups["scheme"].Value + " " + Placeholder;
    }

    private static string AssignmentReplacement(Match match)
    {
        if (!IsSensitiveName(match.Groups["name"].Value))
        {
            return match.Value;
        }

        var separator = match.Groups["sep"].Value;

        return match.Groups["quoted"].Success
            ? match.Groups["name"].Value + separator + "\"" + Placeholder + "\""
            : match.Groups["name"].Value + separator + Placeholder;
    }

    // A truncated private key remains sensitive. Without an END marker, consume the diagnostic tail.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(@"(?<name>\bauthorization\b)(?<sep>\s*[:=]\s*)(?<value>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationLineRegex();

    [GeneratedRegex(@"""(?<name>[A-Za-z0-9_.\-]*)""(?<sep>\s*:\s*)""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex JsonStringValueRegex();

    [GeneratedRegex(@"(?<scheme>\bBearer)\s+(?<value>[A-Za-z0-9\-._~+/=]{8,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStyleKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\bcrsr_[A-Za-z0-9_\-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex CursorKeyRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    // Match only the assignment prefix, then consume a value only for a sensitive name.
    // A public assignment must not swallow a later credential on the same line.
    private static string RedactUnquotedAssignments(string input)
    {
        var result = new StringBuilder();
        var copiedThrough = 0;
        foreach (Match match in UnquotedAssignmentNameRegex().Matches(input))
        {
            if (match.Index < copiedThrough || !IsSensitiveName(match.Groups["name"].Value)) continue;
            var valueStart = match.Index + match.Length;
            if (valueStart >= input.Length || char.IsWhiteSpace(input[valueStart])
                || IsAssignmentValueDelimiter(input[valueStart])) continue;
            var valueEnd = valueStart;
            while (valueEnd < input.Length && !IsAssignmentValueDelimiter(input[valueEnd])) valueEnd++;
            result.Append(input.AsSpan(copiedThrough, match.Index - copiedThrough));
            result.Append(match.Value).Append(Placeholder);
            copiedThrough = valueEnd;
        }
        if (copiedThrough == 0) return input;
        return result.Append(input.AsSpan(copiedThrough)).ToString();
    }

    private static bool IsAssignmentValueDelimiter(char value) => value is
        '\r' or '\n' or '&' or ';' or ',' or '"' or '\'' or '<' or '>' or '[' or ']' or '?';

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_.\-]+)(?<sep>[ \t]*[:=][ \t]*)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnquotedAssignmentNameRegex();

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^\s/?#@]+@", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UrlUserInfoRegex();

    // Diagnostics can contain long unmatched identifiers; avoid retrying every suffix.
    [GeneratedRegex(@"(?<name>[A-Za-z0-9_.\-]+)(?<sep>\s*[:=]\s*)(?:""(?<quoted>[^""\r\n]*)""|(?<value>[^\s&;,""'<>\[\]?]{4,}))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AssignmentRegex();
}
