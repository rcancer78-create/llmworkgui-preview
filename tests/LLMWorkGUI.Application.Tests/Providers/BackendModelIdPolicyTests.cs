using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Providers;

/// <summary>
/// The policy that decides whether a local value may be sent to a backend as <c>Model</c>. Several
/// backends store something that is not a model id in the same column — StarCliProxy a Codex home path,
/// Agy a profile name — so a value is accepted or refused by explicit rules, never repaired.
/// </summary>
public sealed class BackendModelIdPolicyTests
{
    [Theory]
    [InlineData("opencode/space-bunny-free")]
    [InlineData("anthropic/claude-sonnet-4")]
    [InlineData("llama3:8b")]
    [InlineData("gpt-5")]
    [InlineData("qwen3-coder:480b")]
    [InlineData("provider/model.v2-beta")]
    public void TryNormalize_AcceptsRealModelIds(string value)
    {
        Assert.True(BackendModelIdPolicy.TryNormalize(value, out var modelId));
        Assert.Equal(value, modelId);
        Assert.True(BackendModelIdPolicy.IsSelectableModelId(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("model with space")]
    [InlineData("model\nnewline")]
    [InlineData("urn:llmworkgui:secret:provider-key")]
    [InlineData(@"C:\Users\tester\.codex\home")]
    [InlineData(@"..\..\secrets")]
    [InlineData("/etc/opencode/models.json")]
    [InlineData("~/.opencode/model")]
    [InlineData("C:model")]
    [InlineData("C:/Users/tester/model")]
    [InlineData("C:1-model")]
    [InlineData("c:_model")]
    [InlineData("z:模型")]
    public void TryNormalize_RefusesValuesThatAreNotAModelId(string? value)
    {
        Assert.False(BackendModelIdPolicy.TryNormalize(value, out var modelId));
        Assert.Equal(string.Empty, modelId);
        Assert.False(BackendModelIdPolicy.IsSelectableModelId(value));
    }

    [Fact]
    public void TryNormalize_TrimsSurroundingWhitespaceAndRejectsTheRest()
    {
        Assert.True(BackendModelIdPolicy.TryNormalize("  opencode/space-bunny-free  ", out var modelId));
        Assert.Equal("opencode/space-bunny-free", modelId);
    }

    [Fact]
    public void TryNormalize_RefusesAnOverlongValue()
    {
        Assert.False(BackendModelIdPolicy.IsSelectableModelId(new string('a', BackendModelIdPolicy.MaxLength + 1)));
        Assert.True(BackendModelIdPolicy.IsSelectableModelId(new string('a', BackendModelIdPolicy.MaxLength)));
    }
}
