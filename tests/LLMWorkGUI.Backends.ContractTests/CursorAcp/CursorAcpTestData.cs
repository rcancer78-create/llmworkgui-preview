using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal static class CursorAcpTestData
{
    public const string AgentVersion = "2026.09.15-d2fe57e";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static JsonElement ReadHandshakeResult()
    {
        using var document = JsonDocument.Parse(ReadHandshakeResponseText());
        return document.RootElement.GetProperty("response").GetProperty("result").Clone();
    }

    public static JsonElement ReadHandshakeRequest()
    {
        using var document = JsonDocument.Parse(ReadHandshakeResponseText());
        return document.RootElement.GetProperty("request").Clone();
    }

    public static string ReadHandshakeResponseText() =>
        File.ReadAllText(FixturePath("acp-handshake-response.json"));

    public static string ReadModelCatalogText() =>
        File.ReadAllText(FixturePath("acp-model-catalog.json"));

    public static string ReadCapabilitiesText() =>
        File.ReadAllText(FixturePath("acp-capabilities.json"));

    public static JsonElement ReadSessionLoadRequest() =>
        ReadSection("acp-session-load-sample.json", "request");

    public static JsonElement ReadSessionLoadResult() =>
        ReadSection("acp-session-load-sample.json", "response", "result");

    public static JsonElement ReadPromptRequest() =>
        ReadSection("acp-prompt-exchange-sample.json", "request");

    public static JsonElement ReadPromptResult() =>
        ReadSection("acp-prompt-exchange-sample.json", "response", "result");

    public static JsonElement ReadPermissionRequest() =>
        ReadSection("acp-permission-exchange-sample.json", "request");

    public static JsonElement ReadPermissionReply() =>
        ReadSection("acp-permission-exchange-sample.json", "reply");

    public static JsonElement ReadCancelRequest() =>
        ReadSection("acp-cancel-exchange-sample.json", "request");

    public static JsonElement ReadCancelResult() =>
        ReadSection("acp-cancel-exchange-sample.json", "response", "result");

    public static IReadOnlyList<string> ReadStreamingUpdateLines() =>
        File.ReadAllLines(FixturePath("acp-streaming-updates-sample.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    public static JsonRpcNotification ReadStreamingNotification(int index)
    {
        using var document = JsonDocument.Parse(ReadStreamingUpdateLines()[index]);
        var root = document.RootElement;

        return new JsonRpcNotification
        {
            Method = root.GetProperty("method").GetString()!,
            Parameters = root.GetProperty("params").Clone(),
            Id = root.TryGetProperty("id", out var id) ? id.Clone() : null
        };
    }

    public static JsonRpcNotification CreateSessionUpdateNotification(
        string sessionId,
        string updateType,
        string? text = null)
    {
        var update = new JsonObject
        {
            ["type"] = updateType
        };

        if (text is not null)
        {
            update["text"] = text;
        }

        return new JsonRpcNotification
        {
            Method = "session/update",
            Parameters = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["sessionId"] = sessionId,
                ["update"] = update
            })
        };
    }

    private static JsonElement ReadSection(string fileName, params string[] path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
        var element = document.RootElement;

        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.Clone();
    }

    public static string ModelCatalogFixturePath => FixturePath("acp-model-catalog.json");

    public static JsonElement CreateSessionResult(string sessionId) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["sessionId"] = sessionId
        });

    public static JsonElement CreateSessionResultWithIdProperty(string sessionId) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["id"] = sessionId
        });

    public static JsonElement CreateHandshakeResult(Action<JsonObject>? mutate = null)
    {
        var result = (JsonObject)JsonNode.Parse(ReadHandshakeResult().GetRawText())!;

        mutate?.Invoke(result);

        return JsonSerializer.SerializeToElement(result);
    }

    public static string CreateResponseJson(int id, JsonElement result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = JsonNode.Parse(result.GetRawText())
        }.ToJsonString();

    public static string CreateErrorResponseJson(int id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        }.ToJsonString();

    private static string FixturePath(string fileName) =>
        Path.Combine(RepositoryRoot, "docs", "protocols", "cursor", fileName);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }
}
