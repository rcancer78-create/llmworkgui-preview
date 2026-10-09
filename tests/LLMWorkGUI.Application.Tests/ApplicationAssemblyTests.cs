using LLMWorkGUI.Application;
using Xunit;

namespace LLMWorkGUI.Application.Tests;

public class ApplicationAssemblyTests
{
    [Fact]
    public void ApplicationAssembly_IsLoadedFromExpectedAssembly()
    {
        Assert.Equal("LLMWorkGUI.Application", ApplicationAssembly.Instance.GetName().Name);
    }
}
