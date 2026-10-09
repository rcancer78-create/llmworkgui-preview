using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityVisibilityLatencyResetTests
{
    [Fact]
    public void Reset_SeparatesDispatcherAndQuerySamplesFromThePreviousMeasurementWindow()
    {
        var recorder = new ActivityVisibilityLatencyRecorder(TimeProvider.System);
        recorder.AddDispatcherWorkSample(100);
        recorder.AddQueryWorkSample(200);
        recorder.Reset();

        recorder.AddDispatcherWorkSample(3);
        recorder.AddQueryWorkSample(4);

        Assert.Equal(new[] { 3d }, recorder.DrainDispatcherWorkSamples());
        Assert.Equal(new[] { 4d }, recorder.DrainQueryWorkSamples());
        Assert.Equal(0, recorder.DispatcherSampleCount);
        Assert.Equal(0, recorder.QuerySampleCount);
    }
}
