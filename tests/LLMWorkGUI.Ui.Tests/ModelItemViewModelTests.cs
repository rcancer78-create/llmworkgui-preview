using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.App.ViewModels;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ModelItemViewModelTests
{
    [Fact]
    public void InventoryOnly_DoesNotDisplayInventedChatOrReasoningSupport()
    {
        var item = new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("o1-thinking-200k"));
        Assert.Equal("Возможности не подтверждены", item.CapabilitiesSummary);
        Assert.False(item.SupportsReasoning);
        Assert.Empty(item.SupportedReasoningEfforts);
        Assert.Null(item.ContextWindow);
    }

    [Fact]
    public void ExplicitCapabilities_AreDisplayedWithoutAdditionalDefaults()
    {
        var item = new ModelItemViewModel(new DiscoveredModelDetails("model", "Model",
            Capabilities: ModelCapabilityFlags.Chat | ModelCapabilityFlags.ToolCalling));
        Assert.Equal("Chat | Tools", item.CapabilitiesSummary);
    }
}
