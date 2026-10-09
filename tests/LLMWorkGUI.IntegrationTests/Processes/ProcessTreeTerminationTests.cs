using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessTreeTerminationTests
{
    [Fact]
    public async Task ExecuteAsync_UserCancellation_TerminatesDescendantTree()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(
            dataDirectory.Root,
            new ProcessSupervisorOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) });

        var childPidFilePath = harness.GetPath("tree-cancel-child.pid");
        var specification = harness.CreateSpawnChildAndHangSpecification(
            "exec-tree-cancel",
            childPidFilePath);

        using var cancellation = new CancellationTokenSource();
        var executionTask = supervisor.ExecuteAsync(specification, cancellationToken: cancellation.Token);

        var childProcessId = await FakeProcessHarness.WaitForChildPidAsync(
            childPidFilePath,
            TimeSpan.FromSeconds(60));

        Assert.True(FakeProcessHarness.IsProcessAlive(childProcessId));

        cancellation.Cancel();

        var result = await executionTask;

        Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
        Assert.NotNull(result.ProcessId);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            result.ProcessId!.Value,
            TimeSpan.FromSeconds(30)));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            childProcessId,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ExecuteAsync_NaturalExit_TerminatesRemainingDescendants()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var childPidFilePath = harness.GetPath("tree-exit-child.pid");
        var specification = harness.CreateSpawnChildAndExitSpecification(
            "exec-tree-exit",
            childPidFilePath);

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        var childProcessId = await FakeProcessHarness.WaitForChildPidAsync(
            childPidFilePath,
            TimeSpan.FromSeconds(10));

        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            childProcessId,
            TimeSpan.FromSeconds(30)));
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
