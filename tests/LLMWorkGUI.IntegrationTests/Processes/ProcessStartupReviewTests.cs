using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessStartupReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FalseStartWithoutAssociationNeverPublishesStartedOrRetainsRetryOwnership(bool protocol)
    {
        using var data = new TestDirectory();
        using var workspace = new TestDirectory();
        var calls = 0;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = data.Root }, null, null, _ => { Interlocked.Increment(ref calls); return false; });
        var specification = Specification(workspace.Root, protocol);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (protocol)
                await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartProtocolProcessAsync(specification));
            else
            {
                var result = await supervisor.ExecuteAsync(specification);
                Assert.Equal(ProcessTerminationReason.StartupTimeout, result.TerminationReason);
                Assert.Null(result.ProcessId);
                Assert.Null(result.ExitCode);
            }
            await supervisor.WaitForStartupCleanupAsync(specification.ExecutionId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, supervisor.PendingStartupCount);
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateAssociationIsPhysicallyStoppedWhileAbandonedNativeWorkerRemainsBlocked(bool protocol) =>
        // Keep barrier coordination independent of xUnit's limited synchronization context.
        // The native worker safety deadlines and every physical ownership assertion stay intact.
        Task.Run(() => VerifyLateAssociationAsync(protocol));

    private static async Task VerifyLateAssociationAsync(bool protocol)
    {
        using var data = new TestDirectory();
        using var workspace = new TestDirectory();
        using var associate = new ManualResetEventSlim();
        using var returnWorker = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var associated = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
            { StartupGraceWindow = TimeSpan.Zero }), new StorageOptions { AppDataDirectory = data.Root }, null, null, process =>
        {
            entered.TrySetResult();
            try
            {
                if (!associate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Association fixture barrier expired.");
                var success = process.Start();
                var independent = Process.GetProcessById(process.Id);
                _ = independent.SafeHandle;
                associated.TrySetResult(independent);
                if (!returnWorker.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Native return fixture barrier expired.");
                return success;
            }
            finally { returned.TrySetResult(); }
        });
        using var cancelled = new CancellationTokenSource();
        var specification = Specification(workspace.Root, protocol);
        Task call = protocol ? supervisor.StartProtocolProcessAsync(specification, cancelled.Token)
            : supervisor.ExecuteAsync(specification, cancellationToken: cancelled.Token);
        Process? owned = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            if (protocol)
                await Assert.ThrowsAsync<ProcessStartupPendingException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                await call.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ProcessTerminationReason.StartupPending, (await (Task<ProcessExecutionResult>)call).TerminationReason);
            }
            var cleanup = supervisor.WaitForStartupCleanupAsync(specification.ExecutionId);
            Assert.False(cleanup.IsCompleted);
            associate.Set(); // Native OS association occurs only after abandonment was reported.
            owned = await associated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(returned.Task.IsCompleted);
            Assert.False(cleanup.IsCompleted, "Handle/retry ownership must outlive the still-blocked native worker.");
            returnWorker.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, supervisor.PendingStartupCount);
        }
        finally
        {
            associate.Set();
            returnWorker.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (owned is null && associated.Task.IsCompletedSuccessfully) owned = await associated.Task;
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
            await supervisor.WaitForStartupCleanupAsync(specification.ExecutionId).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ProcessStartSpecification Specification(string root, bool protocol) => new()
    {
        ExecutionId = "startup-review",
        FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"],
        WorkingDirectory = root,
        StdinPolicy = protocol ? ProcessStdinPolicy.DirectProtocolTransport : ProcessStdinPolicy.Closed
    };
}
