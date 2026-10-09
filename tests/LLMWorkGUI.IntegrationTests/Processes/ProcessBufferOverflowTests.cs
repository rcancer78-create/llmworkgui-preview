using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessBufferOverflowTests
{
    private const long MemoryLimitBytes = 64 * 1024;
    private const int HeadRetentionBytes = 16 * 1024;
    private const int TailRetentionBytes = 16 * 1024;

    [Fact]
    public async Task ExecuteAsync_OutputExceedingMemoryLimit_RetainsHeadAndTailWithFullSpool()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root, CreateOverflowOptions());

        const int lineCount = 20000;
        var expectedBytes = FakeProcessHarness.ExpectedContinuousOutputBytes(lineCount);
        var specification = harness.CreateContinuousOutputSpecification("exec-overflow", lineCount);

        var result = await supervisor.ExecuteAsync(specification);

        Assert.True(result.OutputOverflowed);
        Assert.Equal(ProcessTerminationReason.BufferOverflow, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        Assert.Equal(expectedBytes, result.StandardOutputBytes);
        Assert.Equal(expectedBytes, new FileInfo(result.StandardOutputLogPath).Length);

        Assert.StartsWith(
            FakeProcessHarness.ContinuousOutputLine,
            result.StandardOutputHead,
            StringComparison.Ordinal);
        Assert.True(result.StandardOutputHead.Length <= HeadRetentionBytes + 2 * 4096);
        Assert.True(result.StandardOutputTail.Length > 0);
        Assert.True(result.StandardOutputTail.Length <= TailRetentionBytes + 2 * 4096);
        Assert.EndsWith("\r\n", result.StandardOutputTail, StringComparison.Ordinal);
        Assert.True(
            result.StandardOutputHead.Length + result.StandardOutputTail.Length < expectedBytes,
            "The in-memory window must be smaller than the full spooled output.");

        var logBytes = await File.ReadAllBytesAsync(result.StandardOutputLogPath);
        Assert.Equal((byte)'0', logBytes[0]);

        var expectedFirstReadBytes = System.Text.Encoding.UTF8.GetBytes(
            FakeProcessHarness.ContinuousOutputLine + "\r\n");
        Assert.Equal(expectedFirstReadBytes, logBytes[..expectedFirstReadBytes.Length]);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuousOutputWithCancellation_ReportsOverflowWithoutLosingSpool()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root, CreateOverflowOptions());

        const int lineCount = 5_000_000;
        var specification = harness.CreateContinuousOutputSpecification("exec-overflow-cancel", lineCount);

        using var cancellation = new CancellationTokenSource();
        var observedBytes = 0L;
        var progress = new CallbackProgress<ProcessOutputEvent>(outputEvent =>
        {
            if (Interlocked.Add(ref observedBytes, outputEvent.BytesCount) >= 256 * 1024)
            {
                cancellation.Cancel();
            }
        });

        var result = await supervisor.ExecuteAsync(specification, progress, cancellation.Token);

        Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
        Assert.True(result.OutputOverflowed);
        Assert.True(result.StandardOutputBytes >= 256 * 1024);

        Assert.True(new FileInfo(result.StandardOutputLogPath).Length > 0);
        Assert.Equal(
            result.StandardOutputBytes,
            new FileInfo(result.StandardOutputLogPath).Length);
        Assert.True(result.StandardOutputHead.Length <= HeadRetentionBytes + 2 * 4096);
        Assert.True(result.StandardOutputTail.Length <= TailRetentionBytes + 2 * 4096);
        Assert.True(
            result.StandardOutputHead.Length + result.StandardOutputTail.Length < MemoryLimitBytes);
    }

    [Fact]
    public void DefaultOptions_UseTenMegabyteMemoryLimitWithHeadAndTailRetention()
    {
        var options = new ProcessSupervisorOptions();

        Assert.Equal(10L * 1024 * 1024, options.OutputMemoryLimitBytes);
        Assert.Equal(256 * 1024, options.OutputHeadRetentionBytes);
        Assert.Equal(256 * 1024, options.OutputTailRetentionBytes);
        Assert.True(options.OutputHeadRetentionBytes + options.OutputTailRetentionBytes < options.OutputMemoryLimitBytes);
    }

    private static ProcessSupervisorOptions CreateOverflowOptions()
    {
        return new ProcessSupervisorOptions
        {
            OutputMemoryLimitBytes = MemoryLimitBytes,
            OutputHeadRetentionBytes = HeadRetentionBytes,
            OutputTailRetentionBytes = TailRetentionBytes,
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500)
        };
    }

    private static ProcessSupervisor CreateSupervisor(
        string appDataDirectory,
        ProcessSupervisorOptions options)
    {
        return new ProcessSupervisor(
            Options.Create(options),
            new StorageOptions { AppDataDirectory = appDataDirectory });
    }
}
