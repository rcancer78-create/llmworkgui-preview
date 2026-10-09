using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Application.Tests;

public sealed class ApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddApplication_RegistersActivityTimelineServiceAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddApplication();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IActivityTimelineService>();
        var second = provider.GetRequiredService<IActivityTimelineService>();

        Assert.IsType<ActivityTimelineService>(first);
        Assert.Same(first, second);
    }
}
