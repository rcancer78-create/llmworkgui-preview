using LLMWorkGUI.ActivityLoadDriver;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityMemoryGateTests
{
    [Theory]
    [InlineData(100, 100, 100, true, 1024, true)]
    [InlineData(101, 100, 100, true, 1024, false)]
    [InlineData(100, 100, 200, true, 1024, false)]
    [InlineData(100, 0, 0, true, 1024, false)]
    [InlineData(100, 100, 100, false, 1024, false)]
    [InlineData(100, 100, 100, true, 0, false)]
    public void MemoryGate_RequiresMeasuredEndAndShippedCapacity(
        int peak, long composed, long shipped, bool measuredEnd, long privateBytes, bool expected)
    {
        var report = new ActivityLoadReport(ActivityLoadOptions.Parse([]), DateTimeOffset.UtcNow)
        {
            WindowStayedWithinCapacity = peak,
            ComposedWindowCapacity = composed,
            ShippedWindowCapacity = shipped
        };
        report.AddCheckpoint(new MemoryCheckpoint("after seed", 1024, privateBytes, 1024));
        if (measuredEnd) report.AddCheckpoint(new MemoryCheckpoint("end", 2048, privateBytes, 2048));
        Assert.Equal(expected, report.MemoryGrowthIsBounded);
    }
}
