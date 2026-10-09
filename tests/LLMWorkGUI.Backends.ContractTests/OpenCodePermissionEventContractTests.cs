using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodePermissionEventContractTests
{
    [Fact]
    public void ActualCapturedEvents_MatchNativeAskedAndRepliedSchemasVerbatim()
    {
        using var stream = typeof(OpenCodePermissionEventContractTests).Assembly.GetManifestResourceStream("OpenCodeNativePermissionEvents");
        Assert.NotNull(stream); using var fixture = JsonDocument.Parse(stream!);
        var events = fixture.RootElement.GetProperty("capturedEvents").EnumerateArray().ToArray(); Assert.Equal(2, events.Length);
        AssertSchema(events[0], fixture.RootElement.GetProperty("schemas").GetProperty("EventPermissionAsked"));
        AssertSchema(events[1], fixture.RootElement.GetProperty("schemas").GetProperty("EventPermissionReplied"));
        var envelope = new OpenCodeEventEnvelope(events[0].GetProperty("type").GetString()!, events[0].GetProperty("properties").Clone(), events[0].GetRawText(), DateTime.UtcNow);
        Assert.True(PermissionRequestedEvent.TryParse(envelope, out var asked));
        var replied = events[1].GetProperty("properties");
        Assert.Equal(asked!.SessionId, replied.GetProperty("sessionID").GetString());
        Assert.Equal(asked.RequestId, replied.GetProperty("requestID").GetString());
        Assert.Equal("reject", replied.GetProperty("reply").GetString());
    }

    private static void AssertSchema(JsonElement value, JsonElement schema)
    {
        switch (schema.GetProperty("type").GetString())
        {
            case "object":
                Assert.Equal(JsonValueKind.Object, value.ValueKind);
                if (schema.TryGetProperty("required", out var required))
                    foreach (var field in required.EnumerateArray()) Assert.True(value.TryGetProperty(field.GetString()!, out _), "Missing captured native field: " + field.GetString());
                if (schema.TryGetProperty("properties", out var properties))
                    foreach (var field in value.EnumerateObject())
                    {
                        if (properties.TryGetProperty(field.Name, out var child)) AssertSchema(field.Value, child);
                        else Assert.False(schema.TryGetProperty("additionalProperties", out var closed) && closed.ValueKind == JsonValueKind.False, "Unknown native closed field: " + field.Name);
                    }
                break;
            case "array":
                Assert.Equal(JsonValueKind.Array, value.ValueKind);
                foreach (var item in value.EnumerateArray()) AssertSchema(item, schema.GetProperty("items"));
                break;
            case "string":
                Assert.Equal(JsonValueKind.String, value.ValueKind);
                if (schema.TryGetProperty("enum", out var allowed)) Assert.Contains(value.GetString(), allowed.EnumerateArray().Select(e => e.GetString()));
                if (schema.TryGetProperty("pattern", out var pattern)) Assert.Matches(pattern.GetString()!, value.GetString()!);
                break;
            default: throw new InvalidOperationException("Unhandled captured schema type; must not silently accept.");
        }
    }

    [Fact]
    public void CurrentNativeRequest_ParsesPermissionPatternsAndToolAssociation()
    {
        using var document = JsonDocument.Parse("""{"id":"per_fixture","sessionID":"ses_fixture","permission":"edit","patterns":["file.txt","other.txt"],"metadata":{},"always":["*"],"tool":{"messageID":"msg_fixture","callID":"call_fixture"}}""");
        var envelope = new OpenCodeEventEnvelope("permission.asked", document.RootElement.Clone(), document.RootElement.GetRawText(), DateTime.UtcNow);
        Assert.True(PermissionRequestedEvent.TryParse(envelope, out var request));
        Assert.Equal("edit", request!.Kind); Assert.Equal("ses_fixture", request.SessionId);
        Assert.Equal("msg_fixture", request.MessageId); Assert.Equal("call_fixture", request.CallId);
        Assert.Equal(new[] { "file.txt", "other.txt" }, request.Patterns);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"id\":\" \"}")]
    public void InvalidProperties_HaveNoParsedRequest(string properties)
    {
        using var document = JsonDocument.Parse(properties);
        Assert.False(PermissionRequestedEvent.TryParse(new OpenCodeEventEnvelope("permission.asked", document.RootElement.Clone(), properties, DateTime.UtcNow), out _));
    }
    [Theory]
    [InlineData("read", NormalizedApprovalKind.ReadFile)]
    [InlineData("glob", NormalizedApprovalKind.ReadFile)]
    [InlineData("grep", NormalizedApprovalKind.ReadFile)]
    [InlineData("edit", NormalizedApprovalKind.WriteFile)]
    [InlineData("bash", NormalizedApprovalKind.ShellCommand)]
    [InlineData("webfetch", NormalizedApprovalKind.NetworkTool)]
    [InlineData("websearch", NormalizedApprovalKind.NetworkTool)]
    [InlineData("external_directory", NormalizedApprovalKind.WorkspaceExpansion)]
    [InlineData("Edit", NormalizedApprovalKind.UnknownHighRisk)]
    [InlineData("custom_tool", NormalizedApprovalKind.UnknownHighRisk)]
    [InlineData(null, NormalizedApprovalKind.UnknownHighRisk)]
    public void Normalization_UsesExactNativeKindsAndDefaultsHighRisk(string? native, NormalizedApprovalKind expected) =>
        Assert.Equal(expected, OpenCodePermissionPolicy.Normalize(native));
    [Fact]
    public void TransportScope_IsNeverAnExtraNativeBodyField()
    {
        using var document = JsonDocument.Parse(OpenCodeSessionJson.SerializePermissionReply(new() { Response = "reject", ScopeSessionId = "ses_fixture" }));
        Assert.Single(document.RootElement.EnumerateObject()); Assert.Equal("reject", document.RootElement.GetProperty("reply").GetString());
    }
}
