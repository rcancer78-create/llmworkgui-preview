using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Repositories;

internal static class QuotaSnapshotPayloadSanitizer
{
    public static string? Redact(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) { return payload; }
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return QuotaDiagnosticRedactor.Redact(payload); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return QuotaDiagnosticRedactor.Redact(payload);
            }
            using var buffer = new MemoryStream();
            var changed = false;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    // Provider-controlled metadata needs the same recursive string redaction.
                    if (CredentialTextRedactor.IsSensitiveName(property.Name))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString() is CredentialTextRedactor.Placeholder or "***REDACTED***")
                        {
                            property.Value.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStringValue("***REDACTED***");
                            changed = true;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = property.Value.GetString()!;
                        var redacted = QuotaDiagnosticRedactor.Redact(text);
                        changed |= !string.Equals(text, redacted, StringComparison.Ordinal);
                        writer.WriteStringValue(redacted);
                    }
                    else
                    {
                        var text = property.Value.GetRawText();
                        var redacted = QuotaDiagnosticRedactor.Redact(text);
                        changed |= !string.Equals(text, redacted, StringComparison.Ordinal);
                        writer.WriteRawValue(redacted);
                    }
                }
                writer.WriteEndObject();
            }
            return changed ? Encoding.UTF8.GetString(buffer.ToArray()) : payload;
        }
    }
}
