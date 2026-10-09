using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class MessagePartUpdatedEvent
{
    public const string EventType = "message.part.updated";

    public const string TextPartType = "text";

    public const string ToolPartType = "tool";

    public const string StepStartPartType = "step-start";

    public const string StepFinishPartType = "step-finish";

    public required string PartId { get; init; }

    public required string SessionId { get; init; }

    public required string MessageId { get; init; }

    public required string PartType { get; init; }

    public string? Text { get; init; }

    public string? CallId { get; init; }

    public string? Tool { get; init; }

    public string? ToolStatus { get; init; }

    public JsonElement ToolInput { get; init; }

    public JsonElement ToolOutput { get; init; }

    public string? Reason { get; init; }

    public OpenCodeTokenUsage? Tokens { get; init; }

    public decimal? Cost { get; init; }

    public long? StartedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public long? CompletedAtUnixMilliseconds { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public static bool TryParse(OpenCodeEventEnvelope envelope, out MessagePartUpdatedEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EventType, StringComparison.Ordinal)
            || !OpenCodeJson.TryGetObjectProperty(envelope.Properties, "part", out var part)
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(part, "id"))
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(part, "messageID"))
            || string.IsNullOrWhiteSpace(OpenCodeJson.GetString(part, "type")))
        {
            result = null;
            return false;
        }

        // Only an absent nested identity may fall back to the concrete envelope identity.
        // Explicit null, blank or malformed identities must never inherit another session.
        var sessionId = part.TryGetProperty("sessionID", out _)
            ? OpenCodeJson.GetString(part, "sessionID")
            : OpenCodeJson.GetString(envelope.Properties, "sessionID");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            result = null;
            return false;
        }

        var state = OpenCodeJson.CloneProperty(part, "state");
        var startedAt = OpenCodeJson.GetObjectProperty(part, "time", "start");
        var completedAt = OpenCodeJson.GetObjectProperty(part, "time", "end");

        result = new MessagePartUpdatedEvent
        {
            PartId = OpenCodeJson.GetString(part, "id") ?? string.Empty,
            SessionId = sessionId,
            MessageId = OpenCodeJson.GetString(part, "messageID") ?? string.Empty,
            PartType = OpenCodeJson.GetString(part, "type") ?? string.Empty,
            Text = OpenCodeJson.GetString(part, "text"),
            CallId = OpenCodeJson.GetString(part, "callID"),
            Tool = OpenCodeJson.GetString(part, "tool"),
            ToolStatus = OpenCodeJson.GetString(state, "status"),
            ToolInput = OpenCodeJson.CloneProperty(state, "input"),
            ToolOutput = OpenCodeJson.CloneProperty(state, "output"),
            Reason = OpenCodeJson.GetString(part, "reason"),
            Tokens = OpenCodeTokenUsage.TryParse(part, out var tokens) ? tokens : null,
            Cost = OpenCodeJson.GetDecimal(part, "cost"),
            StartedAtUnixMilliseconds = startedAt,
            StartedAtUtc = OpenCodeJson.FromUnixMilliseconds(startedAt),
            CompletedAtUnixMilliseconds = completedAt,
            CompletedAtUtc = OpenCodeJson.FromUnixMilliseconds(completedAt)
        };

        return true;
    }
}
