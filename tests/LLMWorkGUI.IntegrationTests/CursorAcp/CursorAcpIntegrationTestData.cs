using System.Text.Json;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

internal static class CursorAcpIntegrationTestData
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static JsonElement ReadHandshakeResult()
    {
        using var document = JsonDocument.Parse(ReadHandshakeResponseText());
        return document.RootElement.GetProperty("response").GetProperty("result").Clone();
    }

    public static JsonElement ReadSessionLoadResult() =>
        ReadFixtureSection("acp-session-load-sample.json", "response", "result");

    public static JsonElement ReadPromptResult() =>
        ReadFixtureSection("acp-prompt-exchange-sample.json", "response", "result");

    public static JsonElement ReadCancelResult() =>
        ReadFixtureSection("acp-cancel-exchange-sample.json", "response", "result");

    public static IReadOnlyList<string> ReadStreamingUpdateLines() =>
        File.ReadAllLines(FixturePath("acp-streaming-updates-sample.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    public static string ReadPermissionRequestLine()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-permission-exchange-sample.json")));
        return JsonSerializer.Serialize(document.RootElement.GetProperty("request"));
    }

    public static string CreateResponseJson(int id, JsonElement result)
    {
        return new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText())
        }.ToJsonString();
    }

    private static JsonElement ReadFixtureSection(string fileName, params string[] path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
        var element = document.RootElement;

        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.Clone();
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(RepositoryRoot, "docs", "protocols", "cursor", fileName);

    private static string ReadHandshakeResponseText() =>
        File.ReadAllText(FixturePath("acp-handshake-response.json"));

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
