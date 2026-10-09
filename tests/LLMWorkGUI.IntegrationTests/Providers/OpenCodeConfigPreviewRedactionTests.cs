using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class OpenCodeConfigPreviewRedactionTests
{
    private readonly OpenCodeConfigService _service = new();
    private readonly CustomProviderSettings _settings = new("chosen", "Chosen", "https://example.test/v1",
        customHeaders: [new("X-Private", "new-header-canary", isSecret: true)]);

    [Fact]
    public void Preview_RedactsOldNewAndOtherProviderSecrets_WithoutMutatingFullConfig()
    {
        const string existing = """
            {"provider": {
              "chosen": {"options": {"apiKey": "old-key-canary", "headers": {"X-Private": "old-header-canary"}}},
              "other": {"options": {"apiKey": "other-key-canary", "headers": {"Authorization": "other-header-canary", "X-Opaque": "opaque-canary"}}}
            }, "nested": [{"token":"nested-token-canary"}]}
            """;
        var result = _service.GeneratePreview(_settings, existing, "new-key-canary");
        foreach (var canary in new[] { "old-key-canary", "old-header-canary", "other-key-canary", "other-header-canary",
            "opaque-canary", "nested-token-canary", "new-key-canary", "new-header-canary" })
        {
            Assert.DoesNotContain(canary, result.RedactedJson);
            Assert.DoesNotContain(canary, result.DiffText);
        }
        var full = JsonNode.Parse(result.GeneratedJson)!;
        Assert.Equal("other-key-canary", full["provider"]!["other"]!["options"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("new-key-canary", full["provider"]!["chosen"]!["options"]!["apiKey"]!.GetValue<string>());
        Assert.True(result.HasChanges);
    }

    [Theory]
    [InlineData("{ broken old-secret-canary")]
    [InlineData("\"old-secret-canary\"")]
    [InlineData("[\"old-secret-canary\"]")]
    public void Preview_InvalidExistingDocument_NeverEchoesRawInput(string existing)
    {
        var failure = Assert.Throws<InvalidDataException>(() =>
            _service.GeneratePreview(_settings, existing, "new-key-canary"));
        Assert.DoesNotContain("old-secret-canary", failure.ToString());
        Assert.DoesNotContain("new-key-canary", failure.ToString());
    }

    [Fact]
    public void Preview_HasChangesUsesUnredactedComparison()
    {
        var existing = _service.MergeConfig("{}", _settings, "old-key-canary");
        Assert.False(_service.GeneratePreview(_settings, existing, "old-key-canary").HasChanges);
        Assert.True(_service.GeneratePreview(_settings, existing, "new-key-canary").HasChanges);
    }
    [Fact]
    public void PreviewHidesCommandArrayAndUrlUserInfoWithoutChangingSavedConfiguration()
    {
        const string existing = """
            {"mcp":{"owned":{"command":["owned-helper","--token","owned-command-canary"]}},
             "provider":{"other":{"options":{"baseURL":"https://owned-user:owned-pass@example.test/v1"}}}}
            """;
        var result = _service.GeneratePreview(_settings, existing, null);
        foreach(var value in new[] { "owned-command-canary", "owned-user", "owned-pass" })
        {
            Assert.DoesNotContain(value,result.RedactedJson);
            Assert.DoesNotContain(value,result.DiffText);
            Assert.Contains(value,result.GeneratedJson);
        }
    }
}
