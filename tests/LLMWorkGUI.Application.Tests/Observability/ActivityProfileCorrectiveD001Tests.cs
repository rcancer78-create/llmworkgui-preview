using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityProfileCorrectiveD001Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void UnrepresentableLoadDurationIsRejectedBeforeReportCountsExist(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityLoadProfile(duration: TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void OfferedEventCountOverflowIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActivityLoadProfile(eventsPerSecond: int.MaxValue, duration: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DefaultCapacityOverflowIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActivityLoadProfile(seedEvents: int.MaxValue, eventsPerSecond: 1, duration: TimeSpan.FromSeconds(1)));
    }
}
