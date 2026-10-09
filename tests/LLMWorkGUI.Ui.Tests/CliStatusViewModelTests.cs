using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CliStatusViewModelTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 9, 22, 8, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsync_WithoutBackends_EntersDegradedStateWithRecommendation()
    {
        var viewModel = CreateViewModel(FakeCliDetectionService.Degraded(FixedTimestamp));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsChecked);
        Assert.True(viewModel.IsDegraded);
        Assert.False(viewModel.HasAnyDetected);
        Assert.False(viewModel.HasDetectionFailed);
        Assert.Equal(2, viewModel.Tools.Count);
        Assert.Equal(0, viewModel.DetectedCount);
        Assert.Equal(CliStatusViewModel.DegradedHeadline, viewModel.Headline);
        Assert.Equal(CliStatusViewModel.DegradedRecommendation, viewModel.Recommendation);
        Assert.Contains("PATH", viewModel.Recommendation, StringComparison.Ordinal);
        Assert.Equal("Обнаружено CLI: 0 из 2", viewModel.DetectionSummary);
        Assert.NotEqual("Не проверялось", viewModel.LastCheckedDisplay);
    }

    [Fact]
    public async Task RefreshAsync_WithDetectedBackends_ClearsDegradedState()
    {
        var viewModel = CreateViewModel(FakeCliDetectionService.AllDetected(FixedTimestamp));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsChecked);
        Assert.False(viewModel.IsDegraded);
        Assert.True(viewModel.HasAnyDetected);
        Assert.Equal(2, viewModel.DetectedCount);
        Assert.Equal(CliStatusViewModel.HealthyHeadline, viewModel.Headline);
        Assert.Equal(string.Empty, viewModel.Recommendation);
        Assert.Contains("OpenCode", viewModel.DetectionSummary, StringComparison.Ordinal);
        Assert.All(viewModel.Tools, tool => Assert.True(tool.IsDetected));
    }

    [Fact]
    public async Task RefreshAsync_WhenDetectionThrows_ReportsFailureWithoutThrowing()
    {
        var detectionService = FakeCliDetectionService.Degraded(FixedTimestamp);
        detectionService.ExceptionToThrow = new InvalidOperationException("private-bare-canary");

        var viewModel = CreateViewModel(detectionService);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.HasDetectionFailed);
        Assert.True(viewModel.IsDegraded);
        Assert.Equal(CliStatusViewModel.DetectionFailedHeadline, viewModel.Headline);
        Assert.DoesNotContain("private-bare-canary", viewModel.DetectionSummary, StringComparison.Ordinal);
        Assert.StartsWith("Проверка не удалась:", viewModel.DetectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_UpdatesLastCheckedFromSnapshotTimestamp()
    {
        var viewModel = CreateViewModel(FakeCliDetectionService.Degraded(FixedTimestamp));

        await viewModel.RefreshAsync();

        Assert.StartsWith("2026-09-22", viewModel.LastCheckedDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialState_IsNotCheckedAndNotDegraded()
    {
        var viewModel = CreateViewModel(FakeCliDetectionService.Degraded(FixedTimestamp));

        Assert.False(viewModel.IsChecked);
        Assert.False(viewModel.IsDegraded);
        Assert.Empty(viewModel.Tools);
        Assert.Equal("Не проверялось", viewModel.LastCheckedDisplay);
        Assert.Equal("Проверка не выполнялась", viewModel.DetectionSummary);
        Assert.Equal(CliStatusViewModel.NotCheckedHeadline, viewModel.Headline);
    }

    private static CliStatusViewModel CreateViewModel(FakeCliDetectionService detectionService)
    {
        return new CliStatusViewModel(detectionService, new FakeTimeProvider { UtcNow = FixedTimestamp });
    }
}
