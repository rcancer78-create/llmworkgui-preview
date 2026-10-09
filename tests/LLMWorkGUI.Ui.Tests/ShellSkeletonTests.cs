using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public class ShellSkeletonTests
{
    [Fact]
    public void AppAssembly_IsLoadedFromExpectedAssembly()
    {
        Assert.Equal("LLMWorkGUI.App", typeof(LLMWorkGUI.App.App).Assembly.GetName().Name);
    }
}
