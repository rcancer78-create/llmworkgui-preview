using System.Text;
using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public static class OpenCodeSessionJson
{
    public const string TextPartType = "text";

    public static string SerializeCreateSessionRequest(OpenCodeCreateSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteOptionalString(writer, "title", request.Title);
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                WriteModelDescriptor(writer, request.Model, "id");
            }
            WriteOptionalString(writer, "agent", request.Agent);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializePromptRequest(OpenCodePromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WriteString("messageID", request.MessageId);

            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                WriteModelDescriptor(writer, request.Model);
            }

            WriteOptionalString(writer, "agent", request.Agent);

            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", TextPartType);
            writer.WriteString("text", request.Prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializePermissionReply(OpenCodePermissionReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.IsStandard)
            throw new ArgumentException("The native permission reply must be once, always or reject.", nameof(reply));

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            // Captured native POST /permission/{requestID}/reply has a closed schema.
            // Remember is legacy DTO metadata; native "always" is session-wide, not an execution rule.
            writer.WriteString("reply", reply.Response);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteModelDescriptor(Utf8JsonWriter writer, string model, string idProperty = "modelID")
    {
        var separator = model.IndexOf('/');

        writer.WritePropertyName("model");
        writer.WriteStartObject();

        if (separator > 0 && separator < model.Length - 1)
        {
            writer.WriteString("providerID", model[..separator]);
            writer.WriteString(idProperty, model[(separator + 1)..]);
        }
        else
        {
            writer.WriteString(idProperty, model);
        }

        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(propertyName, value);
        }
    }
}
