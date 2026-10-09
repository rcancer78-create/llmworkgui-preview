using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessTimeoutTests
{
    [Fact]
    public async Task ExecuteAsync_MissingExecutable_ReportsStartupTimeout()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = new ProcessStartSpecification
        {
            ExecutionId = "exec-missing",
            FileName = harness.GetPath("missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            WorkingDirectory = harness.WorkingDirectory
        };

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.StartupTimeout, result.TerminationReason);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
        Assert.True(File.Exists(result.StandardOutputLogPath));
        Assert.True(File.Exists(result.StandardErrorLogPath));
        Assert.Equal(0, new FileInfo(result.StandardOutputLogPath).Length);
        Assert.Equal(0, new FileInfo(result.StandardErrorLogPath).Length);
    }

    [Fact]
    public async Task ExecuteAsync_TurnTimeout_TerminatesHangingProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();

        var options = new ProcessSupervisorOptions
        {
            TurnTimeout = TimeSpan.FromSeconds(2),
            GracefulShutdownTimeout = TimeSpan.FromSeconds(1)
        };

        var supervisor = CreateSupervisor(dataDirectory.Root, options);
        var specification = harness.CreateHangSpecification("exec-turn-timeout");

        var stopwatch = Stopwatch.StartNew();
        var result = await supervisor.ExecuteAsync(specification);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.TurnTimeout, result.TerminationReason);
        Assert.NotNull(result.ProcessId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ExecuteAsync_InactivityTimeout_TerminatesSilentProcess()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();

        var options = new ProcessSupervisorOptions
        {
            InactivityTimeout = TimeSpan.FromSeconds(1),
            GracefulShutdownTimeout = TimeSpan.FromSeconds(1)
        };

        var supervisor = CreateSupervisor(dataDirectory.Root, options);
        var specification = harness.CreateHangSpecification("exec-inactivity-timeout");

        var stopwatch = Stopwatch.StartNew();
        var result = await supervisor.ExecuteAsync(specification);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.InactivityTimeout, result.TerminationReason);
        Assert.NotNull(result.ProcessId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ExecuteAsync_InactivityTimeoutDisabled_SilentProcessIsNotFailedForSilence()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();

        var supervisor = CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) });
        var specification = harness.CreateHangSpecification("exec-silent");

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        var result = await supervisor.ExecuteAsync(specification, cancellationToken: cancellation.Token);

        Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
        Assert.Equal(0, result.StandardOutputBytes);
        Assert.Equal(0, result.StandardErrorBytes);
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
