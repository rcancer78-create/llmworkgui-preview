using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Cli;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CliDetectionServiceTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 9, 22, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task DetectAsync_WhenNoExecutablesArePresent_ReportsDegradedSnapshot()
    {
        var locator = new FakeCliExecutableLocator();
        var service = CreateService(locator);

        var snapshot = await service.DetectAsync();

        Assert.True(snapshot.IsDegraded);
        Assert.False(snapshot.HasAnyDetected);
        Assert.Equal(0, snapshot.DetectedCount);
        Assert.False(snapshot.OpenCode.IsDetected);
        Assert.False(snapshot.CursorAcp.IsDetected);
        Assert.Equal("Не обнаружен", snapshot.OpenCode.StatusDisplay);
        Assert.Equal(FixedTimestamp, snapshot.DetectedAtUtc);
        Assert.Equal(new[] { "opencode", "cursor-agent" }, locator.RequestedNames);
    }

    [Fact]
    public async Task DetectAsync_WhenOnlyOpenCodeIsPresent_ReportsPartialAvailability()
    {
        var locator = new FakeCliExecutableLocator()
            .WithExecutable("opencode", @"C:\tools\opencode.exe");

        var snapshot = await CreateService(locator).DetectAsync();

        Assert.False(snapshot.IsDegraded);
        Assert.True(snapshot.HasAnyDetected);
        Assert.Equal(1, snapshot.DetectedCount);
        Assert.True(snapshot.OpenCode.IsDetected);
        Assert.Equal(@"C:\tools\opencode.exe", snapshot.OpenCode.ResolvedPath);
        Assert.False(snapshot.CursorAcp.IsDetected);
    }

    [Fact]
    public async Task DetectAsync_WhenBothExecutablesArePresent_ReportsTheirPaths()
    {
        var locator = new FakeCliExecutableLocator()
            .WithExecutable("opencode", @"C:\tools\opencode.cmd")
            .WithExecutable("cursor-agent", @"D:\cursor\cursor-agent.cmd");

        var snapshot = await CreateService(locator).DetectAsync();

        Assert.False(snapshot.IsDegraded);
        Assert.Equal(2, snapshot.DetectedCount);
        Assert.Equal(BackendType.OpenCode, snapshot.OpenCode.Backend);
        Assert.Equal(BackendType.CursorAcp, snapshot.CursorAcp.Backend);
        Assert.Equal(@"D:\cursor\cursor-agent.cmd", snapshot.CursorAcp.ResolvedPath);
        Assert.Equal(@"C:\tools\opencode.cmd", snapshot.OpenCode.PathDisplay);
        Assert.False(snapshot.HasDetectionErrors);
    }

    [Fact]
    public async Task DetectAsync_WhenLocatorThrows_DoesNotThrowAndMarksDetectionFailure()
    {
        var locator = new FakeCliExecutableLocator
        {
            ExceptionToThrow = new InvalidOperationException("PATH probe failed")
        };

        var snapshot = await CreateService(locator).DetectAsync();

        Assert.True(snapshot.IsDegraded);
        Assert.True(snapshot.HasDetectionErrors);
        Assert.Equal("PATH probe failed", snapshot.OpenCode.DetectionError);
        Assert.Equal("PATH probe failed", snapshot.CursorAcp.DetectionError);
    }

    [Fact]
    public async Task DetectAsync_WhenTokenIsCancelled_ThrowsOperationCanceled()
    {
        var service = CreateService(new FakeCliExecutableLocator());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DetectAsync(cancellation.Token));
    }

    [Fact]
    public async Task DetectAsync_UsesTimeProviderTimestamp()
    {
        var timeProvider = new FakeTimeProvider { UtcNow = FixedTimestamp.AddHours(3) };
        var service = new CliDetectionService(new FakeCliExecutableLocator(), timeProvider);

        var snapshot = await service.DetectAsync();

        Assert.Equal(FixedTimestamp.AddHours(3), snapshot.DetectedAtUtc);
    }

    private static CliDetectionService CreateService(FakeCliExecutableLocator locator)
    {
        return new CliDetectionService(locator, new FakeTimeProvider { UtcNow = FixedTimestamp });
    }
}
