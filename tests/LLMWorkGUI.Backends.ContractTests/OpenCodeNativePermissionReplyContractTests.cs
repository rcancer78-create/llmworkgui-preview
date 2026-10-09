using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeNativePermissionReplyContractTests
{
    [Theory]
    [InlineData(OpenCodePermissionReply.Once)]
    [InlineData(OpenCodePermissionReply.Always)]
    [InlineData(OpenCodePermissionReply.Reject)]
    public void SerializedReply_SatisfiesCapturedNativeClosedSchema(string response)
    {
        using var fixtureStream = typeof(OpenCodeNativePermissionReplyContractTests).Assembly.GetManifestResourceStream(
            "LLMWorkGUI.Backends.ContractTests.Fixtures.opencode-native-permission-reply-20261003.json");
        Assert.NotNull(fixtureStream);
        using var fixture = JsonDocument.Parse(fixtureStream!);
        var schema = fixture.RootElement.GetProperty("requestBodySchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        using var body = JsonDocument.Parse(OpenCodeSessionJson.SerializePermissionReply(
            new OpenCodePermissionReply { Response = response, Remember = response == OpenCodePermissionReply.Always }));
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            Assert.True(body.RootElement.TryGetProperty(required.GetString()!, out _),
                "Native required field is missing: " + required.GetString());
        foreach (var field in body.RootElement.EnumerateObject())
            Assert.True(schema.GetProperty("properties").TryGetProperty(field.Name, out _),
                "Native closed schema does not accept field: " + field.Name);
        Assert.Contains(body.RootElement.GetProperty("reply").GetString(),
            schema.GetProperty("properties").GetProperty("reply").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(response, body.RootElement.GetProperty("reply").GetString());
    }
}
