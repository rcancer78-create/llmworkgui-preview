using System.Text.Json.Nodes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpModelCatalogServiceTests
{
    private readonly CursorAcpModelCatalogService _service = new();

    [Fact]
    public void Deserialize_ModelCatalogFixture_ParsesEveryDiscoveredModelAndOverride()
    {
        var catalog = _service.Deserialize(CursorAcpTestData.ReadModelCatalogText());

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("cursor-agent-acp", catalog.Protocol);
        Assert.Equal(CursorAcpTestData.AgentVersion, catalog.AgentVersion);
        Assert.Equal(3, catalog.Models.Count);

        var grok = Assert.Single(catalog.Models, model => model.ModelId == "cursor-grok-4.6-high");
        Assert.Equal("grok", grok.Family);
        Assert.Equal("Grok 4.6 High", grok.DisplayName);
        Assert.Equal("256k", grok.DefaultContext);
        Assert.Equal("256k", grok.MaxContext);

        var grokEffort = Assert.Single(grok.Overrides);
        Assert.Equal("effort", grokEffort.Name);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh" }, grokEffort.Values);
        Assert.Equal("high", grokEffort.Default);
        Assert.Equal(CapabilityState.Supported, grokEffort.CapabilityState);

        var claude = Assert.Single(catalog.Models, model => model.ModelId == "claude-4.6-sonnet");
        Assert.Equal("claude", claude.Family);
        Assert.Equal("Claude 4.6 Sonnet", claude.DisplayName);
        Assert.Equal("200k", claude.DefaultContext);
        Assert.Equal("1m", claude.MaxContext);
        Assert.Equal(2, claude.Overrides.Count);

        var claudeContext = Assert.Single(claude.Overrides, definition => definition.Name == "context");
        Assert.Equal(new[] { "200k", "1m" }, claudeContext.Values);
        Assert.Equal("200k", claudeContext.Default);
        Assert.Equal(CapabilityState.Supported, claudeContext.CapabilityState);

        var claudeEffort = Assert.Single(claude.Overrides, definition => definition.Name == "effort");
        Assert.Equal(new[] { "low", "medium", "high" }, claudeEffort.Values);
        Assert.Equal("medium", claudeEffort.Default);
        Assert.Equal(CapabilityState.Supported, claudeEffort.CapabilityState);

        var gpt = Assert.Single(catalog.Models, model => model.ModelId == "gpt-5.3-codex");
        Assert.Equal("gpt", gpt.Family);
        Assert.Equal("GPT-5.3 Codex", gpt.DisplayName);
        Assert.Equal("272k", gpt.DefaultContext);
        Assert.Equal("272k", gpt.MaxContext);
        Assert.Equal(3, gpt.Overrides.Count);

        var fast = Assert.Single(gpt.Overrides, definition => definition.Name == "fast");
        Assert.Equal(new[] { "on", "off" }, fast.Values);
        Assert.Equal("off", fast.Default);
        Assert.Equal(CapabilityState.Unknown, fast.CapabilityState);
    }

    [Fact]
    public void LoadFromFile_ModelCatalogFixture_MatchesDeserialize()
    {
        var fromFile = _service.LoadFromFile(CursorAcpTestData.ModelCatalogFixturePath);
        var fromText = _service.Deserialize(CursorAcpTestData.ReadModelCatalogText());

        Assert.Equal(fromText.SchemaVersion, fromFile.SchemaVersion);
        Assert.Equal(fromText.Protocol, fromFile.Protocol);
        Assert.Equal(fromText.AgentVersion, fromFile.AgentVersion);
        Assert.Equal(
            fromText.Models.Select(model => model.ModelId),
            fromFile.Models.Select(model => model.ModelId));
    }

    [Fact]
    public void FindModel_IsOrdinalAndReturnsNullForUnknownIds()
    {
        var catalog = _service.Deserialize(CursorAcpTestData.ReadModelCatalogText());

        Assert.NotNull(catalog.FindModel("claude-4.6-sonnet"));
        Assert.Null(catalog.FindModel("Claude-4.6-Sonnet"));
        Assert.Null(catalog.FindModel("claude-4.6-sonnet-unknown"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"protocol\":\"cursor-agent-acp\",\"agentVersion\":\"x\"}")]
    public void Deserialize_MalformedCatalog_IsRejected(string json)
    {
        Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));
    }

    [Fact]
    public void Deserialize_DuplicateModelId_IsRejected()
    {
        var json = MutateCatalog(root =>
        {
            var models = (JsonArray)root["models"]!;
            models.Add(JsonNode.Parse(models[0]!.ToJsonString()));
        });

        var exception = Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_UnrecognizedCapabilityState_IsRejected()
    {
        var json = MutateCatalog(root =>
        {
            var overrideEntry = (JsonObject)((JsonArray)((JsonObject)root["models"]![0]!)["overrides"]!)[0]!;
            overrideEntry["state"] = "Probably";
        });

        var exception = Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));

        Assert.Contains("Probably", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_DefaultValueOutsideDiscoveredValues_IsRejected()
    {
        var json = MutateCatalog(root =>
        {
            var overrideEntry = (JsonObject)((JsonArray)((JsonObject)root["models"]![0]!)["overrides"]!)[0]!;
            overrideEntry["default"] = "extreme";
        });

        var exception = Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));

        Assert.Contains("extreme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_DuplicateOverrideParameter_IsRejected()
    {
        var json = MutateCatalog(root =>
        {
            var overrides = (JsonArray)((JsonObject)root["models"]![1]!)["overrides"]!;
            overrides.Add(JsonNode.Parse(overrides[0]!.ToJsonString()));
        });

        var exception = Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_EmptyOverrideValues_IsRejected()
    {
        var json = MutateCatalog(root =>
        {
            var overrideEntry = (JsonObject)((JsonArray)((JsonObject)root["models"]![0]!)["overrides"]!)[0]!;
            overrideEntry["values"] = new JsonArray();
        });

        var exception = Assert.Throws<CursorAcpCatalogFormatException>(() => _service.Deserialize(json));

        Assert.Contains("at least one value", exception.Message, StringComparison.Ordinal);
    }

    private static string MutateCatalog(Action<JsonObject> mutate)
    {
        var root = (JsonObject)JsonNode.Parse(CursorAcpTestData.ReadModelCatalogText())!;

        mutate(root);

        return root.ToJsonString();
    }
}
