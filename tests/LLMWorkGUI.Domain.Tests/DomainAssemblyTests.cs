using LLMWorkGUI.Domain;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public class DomainAssemblyTests
{
    [Fact]
    public void DomainAssembly_IsLoadedFromExpectedAssembly()
    {
        Assert.Equal("LLMWorkGUI.Domain", DomainAssembly.Instance.GetName().Name);
    }
}
