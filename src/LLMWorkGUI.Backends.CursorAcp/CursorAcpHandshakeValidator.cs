using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Strictly validates the ACP <c>initialize</c> result against ADR-0003 §2 and produces
/// <see cref="CursorAcpHandshakeEvidence"/>. <c>protocolVersion</c> must be the JSON number 1: the
/// string "1" and any other number are rejected as <see cref="CursorAcpProtocolViolationKind.UnsupportedVersion"/>.
/// Required capabilities (<c>loadSession</c>, <c>promptCapabilities.image</c>,
/// <c>sessionCapabilities.list</c>, <c>mcpCapabilities.http/sse</c>) must be present and true.
/// </summary>
public class CursorAcpHandshakeValidator
{
    /// <summary>Only ACP protocol version supported by this baseline (ADR-0003 §2.2).</summary>
    public const int SupportedProtocolVersion = 1;

    public virtual CursorAcpHandshakeEvidence ValidateInitializeResult(JsonElement resultElement)
    {
        if (resultElement.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("The ACP initialize result must be a JSON object.");
        }
        EnsureUniqueFields(resultElement);

        var protocolVersion = ReadProtocolVersion(resultElement);
        var capabilities = ReadCapabilities(resultElement);

        var missing = CollectMissingRequiredCapabilities(capabilities);

        if (missing.Count > 0)
        {
            throw new CursorAcpProtocolViolationException(
                CursorAcpProtocolViolationKind.MissingRequiredCapability,
                "The Cursor ACP agent does not support the capabilities required by ADR-0003 §2.3: " +
                string.Join(", ", missing) + ". The backend stays in degraded mode and is not switched " +
                "to CLI print-mode.");
        }

        return new CursorAcpHandshakeEvidence
        {
            ProtocolVersion = protocolVersion,
            AgentCapabilities = capabilities,
            AuthMethods = ReadAuthMethods(resultElement)
        };
    }

    private static int ReadProtocolVersion(JsonElement resultElement)
    {
        if (!resultElement.TryGetProperty("protocolVersion", out var versionElement))
        {
            throw new CursorAcpProtocolViolationException(
                CursorAcpProtocolViolationKind.UnsupportedVersion,
                "The ACP initialize result did not report protocolVersion. " +
                $"Required: the JSON number {SupportedProtocolVersion}.");
        }

        if (versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version) ||
            version != SupportedProtocolVersion)
        {
            throw new CursorAcpProtocolViolationException(
                CursorAcpProtocolViolationKind.UnsupportedVersion,
                $"The Cursor ACP agent reported an unsupported protocolVersion ({Describe(versionElement)}). " +
                $"Required: the JSON number {SupportedProtocolVersion}; string versions are rejected (ADR-0003 §2.2).");
        }

        return version;
    }

    private static void EnsureUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Malformed("The ACP initialize result contains duplicate object members.");
                EnsureUniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) EnsureUniqueFields(item);
        }
    }

    private static CursorAcpAgentCapabilities ReadCapabilities(JsonElement resultElement)
    {
        if (!resultElement.TryGetProperty("agentCapabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("The ACP initialize result must contain an 'agentCapabilities' object.");
        }

        var mcp = ReadObject(capabilities, "mcpCapabilities");
        var prompt = ReadObject(capabilities, "promptCapabilities");
        var session = ReadObject(capabilities, "sessionCapabilities");

        var sessionList = session is { } sessionObject &&
            sessionObject.TryGetProperty("list", out var list) &&
            list.ValueKind == JsonValueKind.Object;

        return new CursorAcpAgentCapabilities
        {
            LoadSession = ReadBoolean(capabilities, "loadSession"),
            PromptImage = ReadBoolean(prompt, "image"),
            PromptAudio = ReadBoolean(prompt, "audio"),
            PromptEmbeddedContext = ReadBoolean(prompt, "embeddedContext"),
            McpHttp = ReadBoolean(mcp, "http"),
            McpSse = ReadBoolean(mcp, "sse"),
            SessionList = sessionList
        };
    }

    private static List<string> CollectMissingRequiredCapabilities(CursorAcpAgentCapabilities capabilities)
    {
        var missing = new List<string>(5);

        if (!capabilities.LoadSession)
        {
            missing.Add("loadSession");
        }

        if (!capabilities.PromptImage)
        {
            missing.Add("promptCapabilities.image");
        }

        if (!capabilities.SessionList)
        {
            missing.Add("sessionCapabilities.list");
        }

        if (!capabilities.McpHttp)
        {
            missing.Add("mcpCapabilities.http");
        }

        if (!capabilities.McpSse)
        {
            missing.Add("mcpCapabilities.sse");
        }

        return missing;
    }

    private static IReadOnlyList<CursorAcpAuthMethod> ReadAuthMethods(JsonElement resultElement)
    {
        if (!resultElement.TryGetProperty("authMethods", out var authMethods))
        {
            return Array.Empty<CursorAcpAuthMethod>();
        }

        if (authMethods.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("The ACP initialize result 'authMethods' must be an array when present.");
        }

        var methods = new List<CursorAcpAuthMethod>();

        foreach (var item in authMethods.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                continue;
            }

            methods.Add(new CursorAcpAuthMethod
            {
                Id = idElement.GetString()!,
                Name = ReadString(item, "name"),
                Description = ReadString(item, "description")
            });
        }

        return methods;
    }

    private static JsonElement? ReadObject(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement? parent, string propertyName)
    {
        return parent is { } element &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string Describe(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => "\"" + element.GetString() + "\"",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.ValueKind.ToString()
        };
    }

    private static CursorAcpProtocolViolationException Malformed(string message) =>
        new(CursorAcpProtocolViolationKind.MalformedResponse, message);
}
