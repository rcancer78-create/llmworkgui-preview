using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed class OpenCodeEventEnvelope
{
    public const string MalformedType = "malformed";

    public const string UnknownType = "unknown";

    private static readonly JsonElement EmptyProperties = CreateEmptyProperties();

    public OpenCodeEventEnvelope(
        string type,
        JsonElement properties,
        string rawJson,
        DateTime receivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(rawJson);

        Type = type;
        Properties = properties;
        RawJson = rawJson;
        ReceivedAtUtc = receivedAtUtc;
    }

    public string Type { get; }

    public JsonElement Properties { get; }

    public string RawJson { get; }

    public DateTime ReceivedAtUtc { get; }

    public bool IsMalformed => string.Equals(Type, MalformedType, StringComparison.Ordinal);

    // A step-finish can be followed by another model/tool step. Only turn-level evidence
    // gets terminal retention priority in the diagnostic buffer.
    public bool IsTerminal => string.Equals(Type, "session.idle", StringComparison.Ordinal)
        || string.Equals(Type, "session.error", StringComparison.Ordinal)
        || string.Equals(Type, "error", StringComparison.Ordinal);

    public static OpenCodeEventEnvelope Malformed(string rawJson, DateTime receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        return new OpenCodeEventEnvelope(MalformedType, EmptyProperties, rawJson, receivedAtUtc);
    }

    public static JsonElement Empty => EmptyProperties;

    private static JsonElement CreateEmptyProperties()
    {
        using var document = JsonDocument.Parse("{}");

        return document.RootElement.Clone();
    }
}
