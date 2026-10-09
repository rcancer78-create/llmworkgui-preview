using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessSupervisorTests
{
    [Fact]
    public async Task ExecuteAsync_NormalCompletion_CapturesStreamsAndSpoolFiles()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateOutputSpecification(
            "exec-normal",
            "hello-stdout",
            "hello-stderr",
            exitCode: 0);

        var events = new List<ProcessOutputEvent>();
        var progress = new CallbackProgress<ProcessOutputEvent>(events.Add);

        var result = await supervisor.ExecuteAsync(specification, progress);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ProcessId);
        Assert.Contains("hello-stdout", result.StandardOutputHead, StringComparison.Ordinal);
        Assert.Contains("hello-stderr", result.StandardErrorHead, StringComparison.Ordinal);
        Assert.True(result.StandardOutputBytes > 0);
        Assert.True(result.StandardErrorBytes > 0);
        Assert.False(result.OutputOverflowed);
        Assert.Null(result.FailureMessage);
        Assert.True(result.Duration >= TimeSpan.Zero);

        Assert.Contains(events, item => item.StreamKind == ProcessStreamKind.StdOut);
        Assert.Contains(events, item => item.StreamKind == ProcessStreamKind.StdErr);
        Assert.All(events, item => Assert.Equal(TimeSpan.Zero, item.TimestampUtc.Offset));

        Assert.Equal("hello-stdout\r\n", await File.ReadAllTextAsync(result.StandardOutputLogPath));
        Assert.Equal("hello-stderr\r\n", await File.ReadAllTextAsync(result.StandardErrorLogPath));

        var logBytes = await File.ReadAllBytesAsync(result.StandardOutputLogPath);
        Assert.Equal((byte)'h', logBytes[0]);

        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task ExecuteAsync_PassesArgumentsToProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateArgumentSpecification(
            "exec-arguments",
            "first-value",
            "second-value");

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("first=first-value", result.StandardOutputHead, StringComparison.Ordinal);
        Assert.Contains("second=second-value", result.StandardOutputHead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PassesEnvironmentVariables()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateEnvironmentSpecification(
            "exec-environment",
            "LLMWORKGUI_FAKE_VALUE",
            "env-42");

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Contains("value=env-42", result.StandardOutputHead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_IsReported()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateExitCodeSpecification("exec-exit-code", exitCode: 7);

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_TerminatesSilentProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) });

        var specification = harness.CreateHangSpecification("exec-cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(700));

        var stopwatch = Stopwatch.StartNew();
        var result = await supervisor.ExecuteAsync(specification, cancellationToken: cancellation.Token);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
        Assert.Equal(0, result.StandardOutputBytes);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.NotNull(result.ProcessId);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ExecuteAsync_CreatesRunDirectoryOutsideCheckout()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateOutputSpecification("exec-run-directory", "run-directory");

        var result = await supervisor.ExecuteAsync(specification);

        var expectedRunDirectory = Path.Combine(dataDirectory.Root, "runs", "exec-run-directory");

        Assert.Equal(expectedRunDirectory, result.RunDirectory);
        Assert.True(Directory.Exists(result.RunDirectory));
        Assert.Equal(
            Path.Combine(expectedRunDirectory, "stdout.log"),
            result.StandardOutputLogPath);
        Assert.Equal(
            Path.Combine(expectedRunDirectory, "stderr.log"),
            result.StandardErrorLogPath);
        Assert.Equal(Path.GetFullPath(dataDirectory.Root), Path.GetFullPath(Path.Combine(result.RunDirectory, "..", "..")));

        var checkoutRoot = Path.GetFullPath(harness.WorkingDirectory);
        Assert.False(
            result.RunDirectory.StartsWith(checkoutRoot, StringComparison.OrdinalIgnoreCase),
            "Run directory must not be created inside the project checkout root.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunDirectoryWouldBeInsideCheckout_Throws()
    {
        using var harness = new FakeProcessHarness();
        var supervisor = CreateSupervisor(harness.WorkingDirectory);

        var specification = harness.CreateOutputSpecification("exec-inside-checkout", "should-not-run");

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.ExecuteAsync(specification));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/child")]
    [InlineData("nested\\child")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("bad char")]
    [InlineData("bad:colon")]
    public void GetRunDirectory_RejectsUnsafeExecutionIds(string executionId)
    {
        using var dataDirectory = new TestDirectory();

        Assert.Throws<ArgumentException>(() => AppDataPaths.GetRunDirectory(dataDirectory.Root, executionId));
    }

    private static ProcessSupervisor CreateSupervisor(
        string appDataDirectory,
        ProcessSupervisorOptions? options = null)
    {
        return new ProcessSupervisor(
            Options.Create(options ?? new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = appDataDirectory });
    }
}
