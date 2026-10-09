namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class PermissionRequestedEvent
{
    public const string UpdatedEventType = "permission.updated";

    public const string RequestedEventType = "permission.requested";

    public const string AskedEventType = "permission.asked";
    public const string V2AskedEventType = "permission.v2.asked";

    public required string RequestId { get; init; }

    public string? SessionId { get; init; }

    public string? MessageId { get; init; }

    public string? CallId { get; init; }

    public string? Kind { get; init; }

    public string? Title { get; init; }

    public string? Pattern { get; init; }

    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    public long? CreatedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public static bool TryParse(OpenCodeEventEnvelope envelope, out PermissionRequestedEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!IsPermissionEventType(envelope.Type)
            || envelope.Properties.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            result = null;
            return false;
        }

        var requestId = OpenCodeJson.GetString(envelope.Properties, "id")
            ?? OpenCodeJson.GetString(envelope.Properties, "requestID");

        if (string.IsNullOrWhiteSpace(requestId))
        {
            result = null;
            return false;
        }

        var createdAt = OpenCodeJson.GetObjectProperty(envelope.Properties, "time", "created");

        result = new PermissionRequestedEvent
        {
            RequestId = requestId,
            SessionId = OpenCodeJson.GetString(envelope.Properties, "sessionID"),
            MessageId = OpenCodeJson.GetString(envelope.Properties, "messageID")
                ?? GetToolString(envelope.Properties, "messageID"),
            CallId = OpenCodeJson.GetString(envelope.Properties, "callID")
                ?? GetToolString(envelope.Properties, "callID"),
            Kind = OpenCodeJson.GetString(envelope.Properties, "permission")
                ?? OpenCodeJson.GetString(envelope.Properties, "type")
                ?? OpenCodeJson.GetString(envelope.Properties, "action"),
            Title = OpenCodeJson.GetString(envelope.Properties, "title"),
            Pattern = OpenCodeJson.GetString(envelope.Properties, "pattern"),
            Patterns = envelope.Properties.TryGetProperty("patterns", out var patterns)
                && patterns.ValueKind == System.Text.Json.JsonValueKind.Array
                ? patterns.EnumerateArray().Where(p => p.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(p => p.GetString()!).ToArray()
                : Array.Empty<string>(),
            CreatedAtUnixMilliseconds = createdAt,
            CreatedAtUtc = OpenCodeJson.FromUnixMilliseconds(createdAt)
        };

        return true;
    }

    private static string? GetToolString(System.Text.Json.JsonElement properties, string name) =>
        properties.TryGetProperty("tool", out var tool) && tool.ValueKind == System.Text.Json.JsonValueKind.Object
            ? OpenCodeJson.GetString(tool, name) : null;

    private static bool IsPermissionEventType(string type)
    {
        return string.Equals(type, UpdatedEventType, StringComparison.Ordinal)
            || string.Equals(type, RequestedEventType, StringComparison.Ordinal)
            || string.Equals(type, AskedEventType, StringComparison.Ordinal)
            || string.Equals(type, V2AskedEventType, StringComparison.Ordinal);
    }
}
