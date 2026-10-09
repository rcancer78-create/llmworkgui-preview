using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

public sealed partial class ActivityCenterRestartTests
{
    [Theory]
    [InlineData(25, 1, true, 10)]
    [InlineData(25, 2, true, 10)]
    [InlineData(25, 3, false, 5)]
    [InlineData(25, 4, false, 0)]
    [InlineData(20, 2, false, 10)]
    [InlineData(0, 1, false, 0)]
    [InlineData(25, int.MaxValue, false, 0)]
    public async Task DurablePaginationTruncation_OnlyReportsRowsBeyondTheRequestedPage(
        int count, int page, bool expectedTruncation, int expectedItems)
    {
        using var host = BuildHost();
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);
        var journal = host.Services.GetRequiredService<IActivityEventJournal>();
        await journal.AppendRangeAsync(Enumerable.Range(0, count)
            .Select(index => Event($"page:{index:D7}", Now.AddSeconds(index))).ToArray());
        var activity = host.Services.GetRequiredService<IActivityCenterService>();
        var result = activity.QueryPage(page: page, pageSize: 10);
        Assert.Equal(count, result.FilteredCount);
        Assert.Equal(expectedItems, result.Items.Count);
        Assert.Equal(expectedTruncation, result.IsTruncated);
    }
}
