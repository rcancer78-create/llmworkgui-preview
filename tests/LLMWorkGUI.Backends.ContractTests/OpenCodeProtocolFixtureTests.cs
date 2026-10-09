using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeProtocolFixtureTests
{
    private static readonly string[] FixtureFileNames =
    {
        "server-api-inventory.json",
        "session-schema.json",
        "session-export-sample.json",
        "event-stream-sample.jsonl"
    };

    private static readonly string[] ExpectedInventoryOperations =
    {
        "GET /session",
        "POST /session",
        "GET /session/{sessionID}",
        "POST /session/{sessionID}/fork",
        "POST /session/{sessionID}/abort",
        "GET /session/{sessionID}/message",
        "POST /session/{sessionID}/message",
        "POST /session/{sessionID}/prompt_async",
        "GET /event",
        "GET /api/session/{sessionID}/event",
        "POST /permission/{requestID}/reply",
        "GET /api/health",
        "GET /global/health",
        "GET /config/providers",
        "POST /instance/dispose"
    };

    private static readonly string[] ExpectedSessionSchemaProperties =
    {
        "id",
        "slug",
        "projectID",
        "directory",
        "summary",
        "cost",
        "tokens",
        "title",
        "agent",
        "model",
        "version",
        "time",
        "permission"
    };

    private static readonly (string Name, string Pattern)[] ForbiddenPatterns =
    {
        ("OpenAI-style API key", @"\bsk-[A-Za-z0-9_-]{12,}\b"),
        ("GitHub token", @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"),
        ("AWS access key", @"\bAKIA[0-9A-Z]{16}\b"),
        ("Slack token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        ("Bearer credential", @"\bBearer\s+[A-Za-z0-9._~+/-]{16,}"),
        ("Private key block", @"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        ("Windows user profile path", @"[A-Za-z]:\\Users\\"),
        ("POSIX home path", @"/home/[A-Za-z0-9._-]+/")
    };

    [Fact]
    public void ServerApiInventory_ParsesAndCoversRequiredOperations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("server-api-inventory.json")));
        var root = document.RootElement;

        Assert.Equal("1.18.31", root.GetProperty("opencodeVersion").GetString());
        Assert.Equal("/doc", root.GetProperty("openApiSpecPath").GetString());

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in root.GetProperty("endpoints").EnumerateArray())
        {
            var method = endpoint.GetProperty("method").GetString();
            var path = endpoint.GetProperty("path").GetString();

            Assert.False(string.IsNullOrWhiteSpace(method));
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.True(path!.StartsWith("/", StringComparison.Ordinal), $"Endpoint path '{path}' must be absolute.");
            Assert.False(string.IsNullOrWhiteSpace(endpoint.GetProperty("purpose").GetString()));

            Assert.True(operations.Add($"{method} {path}"), $"Duplicate endpoint '{method} {path}'.");
        }

        foreach (var expected in ExpectedInventoryOperations)
        {
            Assert.Contains(expected, operations);
        }

        Assert.NotEmpty(root.GetProperty("missingOperations").EnumerateArray());
    }

    [Fact]
    public void SessionSchema_IsJsonSchemaWithExpectedSessionFields()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("session-schema.json")));
        var root = document.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());

        var properties = root.GetProperty("properties");
        foreach (var expected in ExpectedSessionSchemaProperties)
        {
            Assert.True(properties.TryGetProperty(expected, out _), $"Session schema has no '{expected}' property.");
        }

        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("id", required);
        Assert.Contains("directory", required);
        Assert.Contains("version", required);
    }

    [Fact]
    public void SessionExportSample_ParsesAndSatisfiesRequiredSchemaFields()
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(FixturePath("session-schema.json")));
        using var exportDocument = JsonDocument.Parse(File.ReadAllText(FixturePath("session-export-sample.json")));

        var info = exportDocument.RootElement.GetProperty("info");
        foreach (var required in schemaDocument.RootElement.GetProperty("required").EnumerateArray())
        {
            var name = required.GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(info.TryGetProperty(name!, out _), $"Exported session is missing required field '{name}'.");
        }

        Assert.StartsWith("ses_", info.GetProperty("id").GetString());
        Assert.True(info.TryGetProperty("tokens", out var tokens));
        Assert.Equal(JsonValueKind.Number, tokens.GetProperty("input").ValueKind);

        var messages = exportDocument.RootElement.GetProperty("messages");
        Assert.True(messages.GetArrayLength() >= 2, "Session export sample must contain at least a user and an assistant message.");

        var partTypes = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, message.GetProperty("info").ValueKind);

            foreach (var part in message.GetProperty("parts").EnumerateArray())
            {
                var partType = part.GetProperty("type").GetString();
                Assert.False(string.IsNullOrWhiteSpace(partType));
                partTypes.Add(partType!);
            }
        }

        Assert.Contains("text", partTypes);
        Assert.Contains("tool", partTypes);
        Assert.Contains("step-start", partTypes);
        Assert.Contains("step-finish", partTypes);
    }

    [Fact]
    public void EventStreamSample_ParsesAsJsonLinesWithTypedEnvelopes()
    {
        var eventTypes = new List<string>();
        var partTypes = new List<string>();

        foreach (var line in File.ReadAllLines(FixturePath("event-stream-sample.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var type = root.GetProperty("type").GetString();
            Assert.False(string.IsNullOrWhiteSpace(type));
            Assert.Equal(JsonValueKind.Object, root.GetProperty("properties").ValueKind);

            eventTypes.Add(type!);

            if (string.Equals(type, "message.part.updated", StringComparison.Ordinal))
            {
                var part = root.GetProperty("properties").GetProperty("part");
                var partType = part.GetProperty("type").GetString();
                Assert.False(string.IsNullOrWhiteSpace(partType));
                partTypes.Add(partType!);
            }
        }

        Assert.True(eventTypes.Count >= 10, "Event stream sample is unexpectedly short.");
        Assert.Equal("server.connected", eventTypes[0]);
        Assert.Equal("session.idle", eventTypes[^1]);
        Assert.Contains("text", partTypes);
        Assert.Contains("tool", partTypes);
        Assert.Contains("step-start", partTypes);
        Assert.Contains("step-finish", partTypes);
    }

    [Fact]
    public void Fixtures_ContainNoSecretsOrUserProfilePaths()
    {
        foreach (var fileName in FixtureFileNames)
        {
            var content = File.ReadAllText(FixturePath(fileName));

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                Assert.False(
                    Regex.IsMatch(content, pattern, RegexOptions.CultureInvariant),
                    $"Fixture '{fileName}' matches forbidden pattern '{name}'.");
            }
        }
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "opencode", fileName);
    }

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
