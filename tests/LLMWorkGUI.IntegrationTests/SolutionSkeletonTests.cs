using LLMWorkGUI.Application;
using LLMWorkGUI.Infrastructure;
using LLMWorkGUI.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public class SolutionSkeletonTests
{
    [Fact]
    public void CoreAssemblies_AreDiscoverable()
    {
        Assert.Equal("LLMWorkGUI.Application", ApplicationAssembly.Instance.GetName().Name);
        Assert.Equal("LLMWorkGUI.Infrastructure", InfrastructureAssembly.Instance.GetName().Name);
        Assert.Equal("LLMWorkGUI.Workflows", WorkflowsAssembly.Instance.GetName().Name);
    }
}
