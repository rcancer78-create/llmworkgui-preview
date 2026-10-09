using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class MessageUpdatedEvent
{
    public const string EventType = "message.updated";

    public required string MessageId { get; init; }

    public required string SessionId { get; init; }

    public string? Role { get; init; }

    public string? ParentId { get; init; }

    public string? Finish { get; init; }

    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    /// <summary>Reported identifiers are malformed, duplicated or contradictory; they cannot establish route identity.</summary>
    public bool ModelMetadataInvalid { get; init; }

    public long? CreatedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public long? CompletedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public static bool TryParse(OpenCodeEventEnvelope envelope, out MessageUpdatedEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EventType, StringComparison.Ordinal)
            || !OpenCodeJson.TryGetObjectProperty(envelope.Properties, "info", out var info)
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(info, "id"))
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(info, "sessionID")))
        {
            result = null;
            return false;
        }

        var createdAt = OpenCodeJson.GetObjectProperty(info, "time", "created");
        var completedAt = OpenCodeJson.GetObjectProperty(info, "time", "completed");
        var modelId = ReadReportedIdentifier(info, "modelID", out var invalidModel);
        var providerId = ReadReportedIdentifier(info, "providerID", out var invalidProvider);
        var invalidIdentity = invalidModel || invalidProvider;

        result = new MessageUpdatedEvent
        {
            MessageId = OpenCodeJson.GetString(info, "id") ?? string.Empty,
            SessionId = OpenCodeJson.GetString(info, "sessionID") ?? string.Empty,
            Role = OpenCodeJson.GetString(info, "role"),
            ParentId = OpenCodeJson.GetString(info, "parentID"),
            Finish = OpenCodeJson.GetString(info, "finish"),
            ModelId = invalidIdentity ? null : modelId,
            ProviderId = invalidIdentity ? null : providerId,
            ModelMetadataInvalid = invalidIdentity,
            CreatedAtUnixMilliseconds = createdAt,
            CreatedAtUtc = OpenCodeJson.FromUnixMilliseconds(createdAt),
            CompletedAtUnixMilliseconds = completedAt,
            CompletedAtUtc = OpenCodeJson.FromUnixMilliseconds(completedAt)
        };

        return true;
    }

    private static string? ReadReportedIdentifier(JsonElement info, string name, out bool invalid)
    {
        invalid = false;
        var flat = ReadUnique(info, name, ref invalid);
        string? nested = null;
        var containers = info.EnumerateObject().Where(property => property.NameEquals("model")).ToArray();
        if (containers.Length > 1 || containers.Length == 1 && containers[0].Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            invalid = true;
        else if (containers.Length == 1 && containers[0].Value.ValueKind == JsonValueKind.Object)
            nested = ReadUnique(containers[0].Value, name, ref invalid);
        if (flat is not null && nested is not null && !string.Equals(flat, nested, StringComparison.Ordinal)) invalid = true;
        return invalid ? null : flat ?? nested;
    }

    private static string? ReadUnique(JsonElement info, string name, ref bool invalid)
    {
        var fields = info.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (fields.Length == 0) return null;
        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String)
        { invalid = true; return null; }
        var value = fields[0].Value.GetString();
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
        { invalid = true; return null; }
        return value;
    }
}
