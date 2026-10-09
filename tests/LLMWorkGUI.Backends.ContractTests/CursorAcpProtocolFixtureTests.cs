using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class CursorAcpProtocolFixtureTests
{
    private static readonly string[] FixtureFileNames =
    {
        "acp-handshake-response.json",
        "acp-session-new-schema.json",
        "acp-model-catalog.json",
        "acp-capabilities.json"
    };

    private static readonly string[] ExchangeFixtureFileNames =
    {
        "acp-session-load-sample.json",
        "acp-prompt-exchange-sample.json",
        "acp-permission-exchange-sample.json",
        "acp-cancel-exchange-sample.json"
    };

    private static readonly string[] StreamingFixtureFileNames =
    {
        "acp-streaming-updates-sample.jsonl"
    };

    private static readonly string[] AllowedCapabilityStates =
    {
        "Supported",
        "Unsupported",
        "Unknown"
    };

    private static readonly (string Name, string Pattern)[] ForbiddenPatterns =
    {
        ("OpenAI-style API key", @"\bsk-[A-Za-z0-9_-]{12,}\b"),
        ("GitHub token", @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"),
        ("AWS access key", @"\bAKIA[0-9A-Z]{16}\b"),
        ("Slack token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        ("Cursor API key", @"\bcrsr_[A-Za-z0-9_-]{12,}\b"),
        ("Generic credential assignment", @"\b(api[_-]?key|token|secret|password)\b\s*[:=]\s*""[A-Za-z0-9._\-]{8,}"""),
        ("Bearer credential", @"\bBearer\s+[A-Za-z0-9._~+/-]{16,}"),
        ("Private key block", @"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        ("Windows user profile path", @"[A-Za-z]:\\+Users\\+"),
        ("POSIX home path", @"/home/[A-Za-z0-9._-]+/"),
        ("Non-placeholder email", @"[A-Za-z0-9._%+-]+@(?!example\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
    };

    [Fact]
    public void Fixtures_ParseAsJsonWithCursorProtocolMetadata()
    {
        foreach (var fileName in FixtureFileNames)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
            var root = document.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal("cursor-agent-acp", root.GetProperty("protocol").GetString());
            Assert.Equal("2026.09.15-d2fe57e", root.GetProperty("agentVersion").GetString());
            Assert.Equal("2026-09-22", root.GetProperty("capturedAt").GetString());
        }
    }

    [Fact]
    public void HandshakeResponse_ParsesInitializeResultWithNumericProtocolVersion()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-handshake-response.json")));
        var root = document.RootElement;

        var request = root.GetProperty("request");
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("initialize", request.GetProperty("method").GetString());

        var requestParams = request.GetProperty("params");
        Assert.Equal(JsonValueKind.Number, requestParams.GetProperty("protocolVersion").ValueKind);
        Assert.Equal(1, requestParams.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("LLMWorkGUI", requestParams.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(requestParams.GetProperty("clientInfo").GetProperty("version").GetString()));

        var response = root.GetProperty("response");
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(JsonValueKind.Number, response.GetProperty("id").ValueKind);
        Assert.Equal(1, response.GetProperty("id").GetInt32());

        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Number, result.GetProperty("protocolVersion").ValueKind);
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());

        var agentCapabilities = result.GetProperty("agentCapabilities");
        Assert.True(agentCapabilities.GetProperty("loadSession").GetBoolean());

        var mcpCapabilities = agentCapabilities.GetProperty("mcpCapabilities");
        Assert.True(mcpCapabilities.GetProperty("http").GetBoolean());
        Assert.True(mcpCapabilities.GetProperty("sse").GetBoolean());

        var promptCapabilities = agentCapabilities.GetProperty("promptCapabilities");
        Assert.False(promptCapabilities.GetProperty("audio").GetBoolean());
        Assert.False(promptCapabilities.GetProperty("embeddedContext").GetBoolean());
        Assert.True(promptCapabilities.GetProperty("image").GetBoolean());

        Assert.Equal(
            JsonValueKind.Object,
            agentCapabilities.GetProperty("sessionCapabilities").GetProperty("list").ValueKind);

        var authMethods = result.GetProperty("authMethods");
        Assert.NotEmpty(authMethods.EnumerateArray());
        Assert.Contains(
            authMethods.EnumerateArray(),
            method => string.Equals(method.GetProperty("id").GetString(), "cursor_login", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionNewSchema_RequiresCwdAndMcpServers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-session-new-schema.json")));
        var root = document.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("session/new", root.GetProperty("properties").GetProperty("method").GetProperty("const").GetString());

        var schemaProperties = root.GetProperty("properties");
        var required = schemaProperties.GetProperty("params")
            .GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Contains("cwd", required);
        Assert.Contains("mcpServers", required);

        var paramsProperties = schemaProperties.GetProperty("params").GetProperty("properties");
        Assert.Equal("string", paramsProperties.GetProperty("cwd").GetProperty("type").GetString());
        Assert.Equal("array", paramsProperties.GetProperty("mcpServers").GetProperty("type").GetString());

        var examples = root.GetProperty("examples");
        Assert.NotEmpty(examples.EnumerateArray());

        foreach (var example in examples.EnumerateArray())
        {
            Assert.Equal("session/new", example.GetProperty("method").GetString());

            var exampleParams = example.GetProperty("params");
            var cwd = exampleParams.GetProperty("cwd").GetString();
            Assert.StartsWith("C:\\workspace\\", cwd);
            Assert.Equal(JsonValueKind.Array, exampleParams.GetProperty("mcpServers").ValueKind);
        }

        Assert.Contains(
            examples.EnumerateArray(),
            example => example.GetProperty("params").GetProperty("mcpServers").GetArrayLength() == 0);
    }

    [Fact]
    public void ModelCatalog_ParsesDiscoveredModelsWithSeparateParameterizedOverrides()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-model-catalog.json")));
        var root = document.RootElement;

        var selectionSyntax = root.GetProperty("selectionSyntax");
        var format = selectionSyntax.GetProperty("format").GetString();
        Assert.False(string.IsNullOrWhiteSpace(format));
        Assert.Contains("[", format!, StringComparison.Ordinal);
        Assert.Contains("]", format!, StringComparison.Ordinal);

        var examples = selectionSyntax.GetProperty("examples");
        Assert.NotEmpty(examples.EnumerateArray());

        var overridePattern = new Regex(@"^[^\[\]]+\[[a-z]+=[^\[\]]+\]$", RegexOptions.CultureInvariant);
        foreach (var example in examples.EnumerateArray())
        {
            var value = example.GetString();
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.Matches(overridePattern, value!);
        }

        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        var overrideParameters = new HashSet<string>(StringComparer.Ordinal);
        var models = root.GetProperty("models");
        Assert.True(models.GetArrayLength() >= 3, "Model catalog must list the discovered grok, claude and gpt models.");

        foreach (var model in models.EnumerateArray())
        {
            var modelId = model.GetProperty("modelId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(modelId));
            Assert.True(modelIds.Add(modelId!), $"Duplicate model id '{modelId}'.");
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("displayName").GetString()));

            families.Add(model.GetProperty("family").GetString()!);

            var overrides = model.GetProperty("overrides");
            Assert.True(overrides.GetArrayLength() >= 1, $"Model '{modelId}' must expose its override parameters separately.");

            foreach (var overrideEntry in overrides.EnumerateArray())
            {
                var name = overrideEntry.GetProperty("name").GetString();
                var state = overrideEntry.GetProperty("state").GetString();

                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.Contains(state, AllowedCapabilityStates);
                overrideParameters.Add(name!);
            }
        }

        Assert.Contains("cursor-grok-4.6-high", modelIds);
        Assert.Contains("claude", families);
        Assert.Contains("gpt", families);
        Assert.Contains("context", overrideParameters);
        Assert.Contains("effort", overrideParameters);
    }

    [Fact]
    public void Capabilities_ReportsStdioModesQuotaAndDiagnosticCliFallback()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-capabilities.json")));
        var root = document.RootElement;

        var transport = root.GetProperty("transport");
        Assert.Equal("cursor-agent acp", transport.GetProperty("command").GetString());
        Assert.Equal("Supported", transport.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Number, transport.GetProperty("protocolVersion").ValueKind);
        Assert.Equal(1, transport.GetProperty("protocolVersion").GetInt32());

        var capabilityStates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capability in root.GetProperty("capabilities").EnumerateArray())
        {
            var id = capability.GetProperty("id").GetString();
            var state = capability.GetProperty("state").GetString();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Contains(state, AllowedCapabilityStates);
            Assert.True(capabilityStates.TryAdd(id!, state!), $"Duplicate capability id '{id}'.");
        }

        Assert.True(capabilityStates.TryGetValue("acp.session.new", out var sessionNewState));
        Assert.Equal("Supported", sessionNewState);

        Assert.True(capabilityStates.TryGetValue("quota.api", out var quotaApiState));
        Assert.Equal("Unsupported", quotaApiState);

        Assert.True(capabilityStates.TryGetValue("models.overrides.parameterized", out var overridesState));
        Assert.Equal("Supported", overridesState);

        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mode in root.GetProperty("modes").EnumerateArray())
        {
            modes.Add(mode.GetProperty("id").GetString()!, mode.GetProperty("access").GetString()!);
        }

        Assert.True(modes.TryGetValue("plan", out var planAccess));
        Assert.Equal("read-only", planAccess);

        Assert.True(modes.TryGetValue("ask", out var askAccess));
        Assert.Equal("read-only", askAccess);

        Assert.True(modes.TryGetValue("agent", out var agentAccess));
        Assert.Equal("write", agentAccess);

        var fallback = root.GetProperty("diagnosticCliFallback");
        Assert.Equal("cursor-agent --print --mode ask", fallback.GetProperty("command").GetString());
        Assert.False(fallback.GetProperty("replacesAcp").GetBoolean());
        Assert.True(fallback.GetProperty("visibleToUser").GetBoolean());

        var quotaPolicy = root.GetProperty("quotaPolicy");
        Assert.Equal("Unsupported", quotaPolicy.GetProperty("api").GetString());
        Assert.False(quotaPolicy.GetProperty("inferenceAllowed").GetBoolean());
    }

    [Fact]
    public void Fixtures_ContainNoSecretsOrUserProfilePaths()
    {
        foreach (var fileName in FixtureFileNames
                     .Concat(ExchangeFixtureFileNames)
                     .Concat(StreamingFixtureFileNames))
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

    [Fact]
    public void ExchangeFixtures_ParseAsJsonWithCursorProtocolMetadata()
    {
        foreach (var fileName in ExchangeFixtureFileNames)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
            var root = document.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("cursor-agent-acp", root.GetProperty("protocol").GetString());
            Assert.Equal("2026.09.15-d2fe57e", root.GetProperty("agentVersion").GetString());
            Assert.Equal("2026-09-23", root.GetProperty("capturedAt").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                root.GetProperty("sanitization").GetProperty("workspace").GetString()));
        }
    }

    [Fact]
    public void SessionLoadFixture_RequiresSessionIdCwdAndMcpServers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-session-load-sample.json")));
        var root = document.RootElement;

        var request = root.GetProperty("request");
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/load", request.GetProperty("method").GetString());

        var requestParams = request.GetProperty("params");
        Assert.False(string.IsNullOrWhiteSpace(requestParams.GetProperty("sessionId").GetString()));
        Assert.StartsWith("C:\\workspace\\", requestParams.GetProperty("cwd").GetString());
        Assert.Equal(JsonValueKind.Array, requestParams.GetProperty("mcpServers").ValueKind);

        var result = root.GetProperty("response").GetProperty("result");
        Assert.Equal(
            requestParams.GetProperty("sessionId").GetString(),
            result.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void PromptExchangeFixture_UsesContentBlocksAndModelAndReturnsStopReason()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-prompt-exchange-sample.json")));
        var root = document.RootElement;

        var request = root.GetProperty("request");
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/prompt", request.GetProperty("method").GetString());

        var requestParams = request.GetProperty("params");
        Assert.False(string.IsNullOrWhiteSpace(requestParams.GetProperty("sessionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(requestParams.GetProperty("model").GetString()));

        var prompt = requestParams.GetProperty("prompt");
        Assert.Equal(JsonValueKind.Array, prompt.ValueKind);

        var block = Assert.Single(prompt.EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(block.GetProperty("text").GetString()));

        var result = root.GetProperty("response").GetProperty("result");
        Assert.Equal("endTurn", result.GetProperty("stopReason").GetString());
    }

    [Fact]
    public void PermissionExchangeFixture_RepliesWithOneShotDecisionOnly()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-permission-exchange-sample.json")));
        var root = document.RootElement;

        var request = root.GetProperty("request");
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/request_permission", request.GetProperty("method").GetString());

        var requestParams = request.GetProperty("params");
        var permissionId = requestParams.GetProperty("permissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(permissionId));
        Assert.False(string.IsNullOrWhiteSpace(requestParams.GetProperty("description").GetString()));

        var reply = root.GetProperty("reply");
        Assert.Equal("2.0", reply.GetProperty("jsonrpc").GetString());
        Assert.Equal(permissionId, reply.GetProperty("id").GetString());
        Assert.Equal("allow_once", reply.GetProperty("result").GetProperty("decision").GetString());
        Assert.DoesNotContain(
            "persist",
            reply.GetProperty("result").GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelExchangeFixture_SendsSessionIdAndReturnsNonTerminalAck()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("acp-cancel-exchange-sample.json")));
        var root = document.RootElement;

        var request = root.GetProperty("request");
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/cancel", request.GetProperty("method").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            request.GetProperty("params").GetProperty("sessionId").GetString()));

        var result = root.GetProperty("response").GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(result.TryGetProperty("stopReason", out _));
    }

    [Fact]
    public void StreamingUpdatesFixture_ParsesAsSessionUpdateJsonLines()
    {
        var lines = File.ReadAllLines(FixturePath("acp-streaming-updates-sample.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        Assert.True(lines.Length >= 4, "The streaming fixture must cover text, thought, tool_call and status updates.");

        var updateTypes = new List<string>();

        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.Equal("session/update", root.GetProperty("method").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                root.GetProperty("params").GetProperty("sessionId").GetString()));

            updateTypes.Add(root.GetProperty("params").GetProperty("update").GetProperty("type").GetString()!);
        }

        Assert.Contains("text", updateTypes);
        Assert.Contains("thought", updateTypes);
        Assert.Contains("tool_call", updateTypes);
        Assert.Contains("status", updateTypes);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "cursor", fileName);
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
