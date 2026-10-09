namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class SessionUpdatedEvent
{
    public const string EventType = "session.updated";

    public required string SessionId { get; init; }

    public string? Title { get; init; }

    public string? Directory { get; init; }

    public long? UpdatedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public static bool TryParse(OpenCodeEventEnvelope envelope, out SessionUpdatedEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EventType, StringComparison.Ordinal)
            || !OpenCodeJson.TryGetObjectProperty(envelope.Properties, "info", out var info)
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(info, "id")))
        {
            result = null;
            return false;
        }

        var updatedAt = OpenCodeJson.GetObjectProperty(info, "time", "updated");

        result = new SessionUpdatedEvent
        {
            SessionId = OpenCodeJson.GetString(info, "id") ?? string.Empty,
            Title = OpenCodeJson.GetString(info, "title"),
            Directory = OpenCodeJson.GetString(info, "directory"),
            UpdatedAtUnixMilliseconds = updatedAt,
            UpdatedAtUtc = OpenCodeJson.FromUnixMilliseconds(updatedAt)
        };

        return true;
    }
}
