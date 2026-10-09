using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class UnconfirmedModelCapabilityTests
{
    [Theory]
    [InlineData("o1")]
    [InlineData("gpt-4o-128k")]
    [InlineData("claude-3-5-sonnet-200k")]
    [InlineData("gemini-2.0-thinking-1m")]
    [InlineData("deepseek-r1-fast")]
    [InlineData("random-embedding-model")]
    public void InventoryIdentity_DoesNotProveAnyModelCapability(string id)
    {
        var model = ModelCapabilityDetector.DetectCapabilities(id, "Vision Tools Reasoning", "provider");

        Assert.Equal(id, model.Id);
        Assert.Equal("Vision Tools Reasoning", model.Name);
        Assert.Equal(ModelCapabilityFlags.None, model.Capabilities);
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Null(model.ContextWindow);
        Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_effort", "high"));
    }

    [Fact]
    public void ExplicitReasoningList_PreservesExactProviderValues()
    {
        var model = new DiscoveredModelDetails("synthetic", "Synthetic",
            Capabilities: ModelCapabilityFlags.ReasoningVariants,
            SupportedReasoningEfforts: ["max", "high"]);

        ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_effort", "max");
        Assert.Throws<ArgumentException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_effort", "HIGH"));
        Assert.Throws<ArgumentException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_effort", "medium"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    [InlineData("")]
    [InlineData(" ")]
    public void ReasoningSelection_RequiresAConcreteSupportedValue(object? value)
    {
        var model = new DiscoveredModelDetails("synthetic", "Synthetic",
            Capabilities: ModelCapabilityFlags.ReasoningVariants,
            SupportedReasoningEfforts: ["high"]);
        Assert.Throws<ArgumentException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_effort", value));
    }
}
