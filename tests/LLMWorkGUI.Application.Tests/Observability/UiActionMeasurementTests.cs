using LLMWorkGUI.ActivityLoadDriver;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class UiActionMeasurementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedActionCannotBecomeSuccessfulWhenSettledDescriptionSucceeds(bool throws)
    {
        var settles = 0;
        var descriptions = 0;
        var failures = 0;
        var result = await UiActionMeasurement.MeasureAsync("search",
            () => throws ? throw new InvalidOperationException("synthetic action failure") : "NOT PERFORMED: synthetic action failure",
            () => { descriptions++; return "42 displayed rows"; },
            callback => Task.FromResult(callback()),
            () => { settles++; return Task.CompletedTask; },
            _ => failures++);
        var report = new ActivityLoadReport(ActivityLoadOptions.Parse([]), DateTimeOffset.UnixEpoch);
        report.AddUiAction(result);

        Assert.False(report.UiActionsAllSucceeded);
        Assert.Equal(1, report.UiActionsNotPerformed);
        Assert.Contains("synthetic action failure", result.Note, StringComparison.Ordinal);
        Assert.Equal(2, settles);
        Assert.Equal(1, descriptions);
        Assert.Equal(throws ? 1 : 0, failures);
    }
}
