namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class SessionIdleEvent
{
    public const string EventType = "session.idle";

    public required string SessionId { get; init; }

    public static bool TryParse(OpenCodeEventEnvelope envelope, out SessionIdleEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EventType, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(OpenCodeJson.GetString(envelope.Properties, "sessionID")))
        {
            result = null;
            return false;
        }

        result = new SessionIdleEvent
        {
            SessionId = OpenCodeJson.GetString(envelope.Properties, "sessionID") ?? string.Empty
        };

        return true;
    }
}
