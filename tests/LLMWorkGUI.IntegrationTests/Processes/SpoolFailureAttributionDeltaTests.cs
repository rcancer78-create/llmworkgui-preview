using System.Diagnostics;
using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Watchdogs;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class SpoolFailureAttributionDeltaTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task DiskCaptureFailureDoesNotChargeModelBudgetUnlessAnActualRetentionLimitAlsoOverflowed(bool diskFault, bool capFault) =>
        Task.Run(() => VerifyAsync(diskFault, capFault));

    private static async Task VerifyAsync(bool diskFault, bool capFault)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        const string marker = "owned-capture-final-marker";
        const long payloadBytes = 8192L * 8;
        var script = checkout.GetPath("capture.ps1");
        await File.WriteAllTextAsync(script,
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $chunk = 'x' * 8192; " +
            "for ($i=0; $i -lt 8; $i++) { [Console]::Out.Write($chunk) }; [Console]::Out.Write('" + marker + "'); exit 0;",
            new UTF8Encoding(true));
        const string executionId = "owned-capture-attribution";
        if (diskFault)
            Directory.CreateDirectory(Path.Combine(AppDataPaths.GetRunDirectory(data.Root, executionId), ProcessSupervisorOptions.StandardOutputFileName));
        var associated = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var real = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            OutputMemoryLimitBytes = capFault ? 32 * 1024 : 1024 * 1024,
            OutputHeadRetentionBytes = 1024, OutputTailRetentionBytes = 1024,
            TurnTimeout = TimeSpan.FromSeconds(15), GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100)
        }), new StorageOptions { AppDataDirectory = data.Root }, null, null, process =>
        {
            var started = process.Start();
            var owned = Process.GetProcessById(process.Id); _ = owned.SafeHandle;
            associated.TrySetResult(owned);
            return started;
        });
        var supervisor = new RecordingSupervisor(real);
        using var cancellation = new CancellationTokenSource();
        var watching = new ExecutionWatchdog(supervisor).WatchAsync(new()
        {
            Specification = new()
            {
                ExecutionId = executionId, WorkingDirectory = checkout.Root,
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]
            }, SessionConfirmationTimeout = null
        }, turnCancellationToken: cancellation.Token);
        Process? ownedProcess = null;
        try
        {
            ownedProcess = await associated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var turn = await watching.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(ownedProcess.HasExited);
            var process = Assert.IsType<ProcessExecutionResult>(supervisor.Result);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(0, turn.ExitCode);
            Assert.Equal(payloadBytes + marker.Length, process.StandardOutputBytes);
            Assert.Equal(payloadBytes + marker.Length, supervisor.ProgressBytes);
            Assert.EndsWith(marker, process.StandardOutputHead + process.StandardOutputTail, StringComparison.Ordinal);
            Assert.True(process.OutputOverflowed);
            Assert.Equal(diskFault, process.OutputCaptureIncomplete);
            Assert.Equal(capFault, process.OutputLimitExceeded);
            Assert.Equal(ProcessTerminationReason.BufferOverflow, process.TerminationReason);
            Assert.Equal(ExecutionTurnOutcome.BufferOverflow, turn.Outcome);
            Assert.Equal(capFault, turn.ConsumesModelRetryBudget);
        }
        finally
        {
            cancellation.Cancel();
            if (ownedProcess is null && associated.Task.IsCompletedSuccessfully) ownedProcess = await associated.Task;
            if (ownedProcess is not null)
            {
                if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true);
                await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            try { await watching.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { await real.WaitForStartupCleanupAsync(executionId).WaitAsync(TimeSpan.FromSeconds(15)); ownedProcess?.Dispose(); }
        }
    }

    private sealed class RecordingSupervisor(ProcessSupervisor inner) : IProcessSupervisor
    {
        private long _progressBytes;
        public long ProgressBytes => Interlocked.Read(ref _progressBytes);
        public ProcessExecutionResult? Result { get; private set; }
        public async Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? progress = null, CancellationToken cancellationToken = default) =>
            Result = await inner.ExecuteAsync(specification, new CountingProgress(this, progress), cancellationToken);
        private sealed class CountingProgress(RecordingSupervisor owner, IProgress<ProcessOutputEvent>? downstream) : IProgress<ProcessOutputEvent>
        {
            public void Report(ProcessOutputEvent value)
            {
                if (value.StreamKind == ProcessStreamKind.StdOut) Interlocked.Add(ref owner._progressBytes, value.BytesCount);
                downstream?.Report(value);
            }
        }
    }
}
