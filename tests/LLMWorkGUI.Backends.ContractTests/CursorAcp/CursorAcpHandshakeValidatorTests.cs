using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpHandshakeValidatorTests
{
    private readonly CursorAcpHandshakeValidator _validator = new();

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("agentCapabilities")]
    [InlineData("loadSession")]
    [InlineData("image")]
    [InlineData("list")]
    [InlineData("http")]
    [InlineData("authMethods")]
    [InlineData("authId")]
    public void ValidateInitializeResult_RejectsDuplicateDecisionFields(string field)
    {
        const string capabilities = """
            {"loadSession":true,"promptCapabilities":{"image":true},
             "sessionCapabilities":{"list":{}},"mcpCapabilities":{"http":true,"sse":true}}
            """;
        var json = "{\"protocolVersion\":1,\"agentCapabilities\":" + capabilities +
            ",\"authMethods\":[{\"id\":\"cursor_login\"}]}";
        json = field switch
        {
            "protocolVersion" => json.Replace("\"protocolVersion\":1", "\"protocolVersion\":2,\"protocolVersion\":1", StringComparison.Ordinal),
            "agentCapabilities" => json.Replace("\"agentCapabilities\":", "\"agentCapabilities\":{},\"agentCapabilities\":", StringComparison.Ordinal),
            "loadSession" => json.Replace("\"loadSession\":true", "\"loadSession\":false,\"loadSession\":true", StringComparison.Ordinal),
            "image" => json.Replace("\"image\":true", "\"image\":false,\"image\":true", StringComparison.Ordinal),
            "list" => json.Replace("\"list\":{}", "\"list\":null,\"list\":{}", StringComparison.Ordinal),
            "http" => json.Replace("\"http\":true", "\"http\":false,\"http\":true", StringComparison.Ordinal),
            "authMethods" => json.Replace("\"authMethods\":", "\"authMethods\":[],\"authMethods\":", StringComparison.Ordinal),
            _ => json.Replace("\"id\":\"cursor_login\"", "\"id\":\"different_account\",\"id\":\"cursor_login\"", StringComparison.Ordinal)
        };
        using var document = JsonDocument.Parse(json);
        var error = Assert.Throws<CursorAcpProtocolViolationException>(() =>
            _validator.ValidateInitializeResult(document.RootElement));
        Assert.Equal(CursorAcpProtocolViolationKind.MalformedResponse, error.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_FixtureResult_ProducesTypedEvidence()
    {
        var evidence = _validator.ValidateInitializeResult(CursorAcpTestData.ReadHandshakeResult());

        Assert.Equal(1, evidence.ProtocolVersion);
        Assert.True(evidence.AgentCapabilities.LoadSession);
        Assert.True(evidence.AgentCapabilities.PromptImage);
        Assert.False(evidence.AgentCapabilities.PromptAudio);
        Assert.False(evidence.AgentCapabilities.PromptEmbeddedContext);
        Assert.True(evidence.AgentCapabilities.McpHttp);
        Assert.True(evidence.AgentCapabilities.McpSse);
        Assert.True(evidence.AgentCapabilities.SessionList);

        var authMethod = Assert.Single(evidence.AuthMethods);
        Assert.Equal("cursor_login", authMethod.Id);
        Assert.Equal("Cursor Login", authMethod.Name);
        Assert.False(string.IsNullOrWhiteSpace(authMethod.Description));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("v1")]
    public void ValidateInitializeResult_StringProtocolVersion_IsUnsupportedVersion(string version)
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node["protocolVersion"] = version);

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.UnsupportedVersion, exception.Kind);
        Assert.Contains("protocolVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInitializeResult_OtherNumericProtocolVersion_IsUnsupportedVersion()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node["protocolVersion"] = 2);

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.UnsupportedVersion, exception.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_MissingProtocolVersion_IsUnsupportedVersion()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node.Remove("protocolVersion"));

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.UnsupportedVersion, exception.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_NonObjectResult_IsMalformedResponse()
    {
        var result = JsonSerializer.SerializeToElement("not-an-object");

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_MissingAgentCapabilities_IsMalformedResponse()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node.Remove("agentCapabilities"));

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_LoadSessionFalse_ReportsMissingRequiredCapability()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(
            node => GetCapabilities(node)["loadSession"] = false);

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MissingRequiredCapability, exception.Kind);
        Assert.Contains("loadSession", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInitializeResult_SessionListMissing_ReportsMissingRequiredCapability()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(
            node => GetObject(GetCapabilities(node), "sessionCapabilities").Remove("list"));

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MissingRequiredCapability, exception.Kind);
        Assert.Contains("sessionCapabilities.list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInitializeResult_PromptImageFalse_ReportsMissingRequiredCapability()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(
            node => GetObject(GetCapabilities(node), "promptCapabilities")["image"] = false);

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MissingRequiredCapability, exception.Kind);
        Assert.Contains("promptCapabilities.image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInitializeResult_McpCapabilitiesMissing_ReportsBothRequiredMcpCapabilities()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(
            node => GetCapabilities(node).Remove("mcpCapabilities"));

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MissingRequiredCapability, exception.Kind);
        Assert.Contains("mcpCapabilities.http", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mcpCapabilities.sse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInitializeResult_NoAuthMethods_ProducesEmptyEvidenceList()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node.Remove("authMethods"));

        var evidence = _validator.ValidateInitializeResult(result);

        Assert.Empty(evidence.AuthMethods);
    }

    [Fact]
    public void ValidateInitializeResult_NonArrayAuthMethods_IsMalformedResponse()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node["authMethods"] = "cursor_login");

        var exception = Assert.Throws<CursorAcpProtocolViolationException>(
            () => _validator.ValidateInitializeResult(result));

        Assert.Equal(CursorAcpProtocolViolationKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public void ValidateInitializeResult_AuthMethodWithoutId_IsSkipped()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node =>
        {
            var methods = (JsonArray)node["authMethods"]!;
            methods.Add(new JsonObject { ["name"] = "Unnamed method" });
        });

        var evidence = _validator.ValidateInitializeResult(result);

        Assert.Single(evidence.AuthMethods);
        Assert.Equal("cursor_login", evidence.AuthMethods[0].Id);
    }

    private static JsonObject GetCapabilities(JsonObject result) =>
        (JsonObject)result["agentCapabilities"]!;

    private static JsonObject GetObject(JsonObject parent, string propertyName) =>
        (JsonObject)parent[propertyName]!;
}
