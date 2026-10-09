namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class ServerConnectedEvent
{
    public const string EventType = "server.connected";

    public static bool TryParse(OpenCodeEventEnvelope envelope, out ServerConnectedEvent? result)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.Type, EventType, StringComparison.Ordinal))
        {
            result = null;
            return false;
        }

        result = new ServerConnectedEvent();
        return true;
    }
}
