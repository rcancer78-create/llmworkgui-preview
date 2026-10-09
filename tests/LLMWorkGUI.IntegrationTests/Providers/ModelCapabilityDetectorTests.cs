using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class ModelCapabilityDetectorTests
{
    [Theory]
    [InlineData("o1", false)]
    [InlineData("o1-mini", false)]
    [InlineData("o3-mini", false)]
    [InlineData("deepseek-r1", false)]
    [InlineData("deepseek-ai/DeepSeek-R1-Distill-Qwen-32B", false)]
    [InlineData("qwq-32b-preview", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("claude-3-5-sonnet", false)]
    public void DetectCapabilities_ReasoningVariants_AreNotInferred(string modelId, bool expectsReasoning)
    {
        var details = ModelCapabilityDetector.DetectCapabilities(modelId);

        Assert.Equal(expectsReasoning, details.SupportsReasoning);
        Assert.Empty(details.SupportedReasoningEfforts);
    }

    [Theory]
    [InlineData("gpt-4o", false)]
    [InlineData("claude-3-5-sonnet-20241022", false)]
    [InlineData("gemini-1.5-pro", false)]
    [InlineData("llava-v1.6-34b", false)]
    [InlineData("llama-3-8b", false)]
    [InlineData("deepseek-coder-6.7b", false)]
    public void DetectCapabilities_Vision_IsNotInferred(string modelId, bool expectsVision)
    {
        var details = ModelCapabilityDetector.DetectCapabilities(modelId);

        Assert.Equal(expectsVision, details.SupportsVision);
    }

    [Theory]
    [InlineData("gpt-4o", false)]
    [InlineData("claude-3-haiku", false)]
    [InlineData("llama-3.1-70b-instruct", false)]
    [InlineData("random-embedding-model", false)]
    public void DetectCapabilities_ToolCalling_IsNotInferred(string modelId, bool expectsTools)
    {
        var details = ModelCapabilityDetector.DetectCapabilities(modelId);

        Assert.Equal(expectsTools, details.SupportsToolCalling);
    }

    [Fact]
    public void ValidateOptionSupported_ReasoningOnNonReasoningModel_ThrowsInvalidOperationException()
    {
        var standardModel = ModelCapabilityDetector.DetectCapabilities("gpt-4o");

        Assert.False(standardModel.SupportsReasoning);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(standardModel, "reasoning_effort", "high"));

        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void ValidateOptionSupported_InvalidReasoningEffort_ThrowsArgumentException()
    {
        var reasoningModel = new DiscoveredModelDetails("synthetic", "Synthetic",
            Capabilities: ModelCapabilityFlags.ReasoningVariants,
            SupportedReasoningEfforts: ["low", "high"]);

        Assert.True(reasoningModel.SupportsReasoning);

        var ex = Assert.Throws<ArgumentException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(reasoningModel, "reasoning_effort", "ultra-extreme"));

        Assert.Contains("Allowed", ex.Message);
    }

    [Fact]
    public void ValidateOptionSupported_VisionOnTextModel_ThrowsInvalidOperationException()
    {
        var textModel = ModelCapabilityDetector.DetectCapabilities("llama-3-8b");

        Assert.False(textModel.SupportsVision);

        Assert.Throws<InvalidOperationException>(() =>
            ModelCapabilityDetector.ValidateOptionSupported(textModel, "vision"));
    }

    [Fact]
    public void IsOptionSupported_ReturnsExpectedFlags()
    {
        var o1 = new DiscoveredModelDetails("reasoning", "Reasoning",
            Capabilities: ModelCapabilityFlags.ReasoningVariants,
            SupportedReasoningEfforts: ["high"]);
        var gpt4o = new DiscoveredModelDetails("multimodal", "Multimodal",
            Capabilities: ModelCapabilityFlags.Vision | ModelCapabilityFlags.ToolCalling);

        Assert.True(ModelCapabilityDetector.IsOptionSupported(o1, "reasoning_effort"));
        Assert.False(ModelCapabilityDetector.IsOptionSupported(gpt4o, "reasoning_effort"));

        Assert.True(ModelCapabilityDetector.IsOptionSupported(gpt4o, "vision"));
        Assert.True(ModelCapabilityDetector.IsOptionSupported(gpt4o, "tools"));
    }
}
