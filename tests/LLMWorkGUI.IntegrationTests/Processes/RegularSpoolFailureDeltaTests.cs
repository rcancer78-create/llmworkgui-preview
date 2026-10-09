using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class RegularSpoolFailureDeltaTests
{
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public Task RegularSpoolFaultDoesNotStopPhysicalDrainOrReportSuccessfulCapture(
        bool stderr, bool failFlush, bool injectFault, bool failOpening) =>
        Task.Run(() => VerifyAsync(stderr, failFlush, injectFault, failOpening));

    private static async Task VerifyAsync(bool stderr, bool failFlush, bool injectFault, bool failOpening)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        const string marker = "owned-output-tail";
        const long payloadBytes = 8192L * 256;
        var script = Path.Combine(checkout.Root, "regular-spool-failure.ps1");
        var console = stderr ? "Error" : "Out";
        await File.WriteAllTextAsync(script,
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
            "$chunk = 'x' * 8192; for ($i = 0; $i -lt 256; $i++) { [Console]::" + console + ".Write($chunk) }; " +
            "[Console]::" + console + ".Write('" + marker + "');", new UTF8Encoding(true));
        var logs = new WarningLog();
        var progress = new CountingProgress(stderr ? ProcessStreamKind.StdErr : ProcessStreamKind.StdOut);
        var associated = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultingOwnedFile? faulting = null;
        var actualOpenFailure = false;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            OutputMemoryLimitBytes = 4 * 1024 * 1024,
            InactivityTimeout = TimeSpan.FromSeconds(5),
            TurnTimeout = TimeSpan.FromSeconds(15),
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100)
        }), new StorageOptions { AppDataDirectory = data.Root }, null, logs, process =>
        {
            var started = process.Start();
            var owned = Process.GetProcessById(process.Id);
            _ = owned.SafeHandle;
            associated.TrySetResult(owned);
            return started;
        }, regularOutputSinkFactory: path =>
        {
            if (failOpening && Path.GetFileName(path) == "stdout.log")
            {
                Directory.CreateDirectory(path); // Actual owned filesystem denial: a directory is not a file.
                try { return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read); }
                catch (UnauthorizedAccessException) { actualOpenFailure = true; throw; }
            }
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (injectFault && Path.GetFileName(path) == (stderr ? "stderr.log" : "stdout.log"))
                return faulting = new FaultingOwnedFile(file, failFlush);
            return file;
        });
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "owned-regular-spool-" + stderr + "-" + injectFault,
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            WorkingDirectory = checkout.Root,
            StdinPolicy = ProcessStdinPolicy.Closed
        };
        using var cancel = new CancellationTokenSource();
        var execution = supervisor.ExecuteAsync(specification, progress, cancel.Token);
        Process? ownedProcess = null;
        try
        {
            ownedProcess = await associated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(ownedProcess.HasExited);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(payloadBytes + marker.Length, stderr ? result.StandardErrorBytes : result.StandardOutputBytes);
            Assert.Equal(payloadBytes + marker.Length, progress.Bytes);
            Assert.EndsWith(marker, stderr ? result.StandardErrorHead + result.StandardErrorTail
                : result.StandardOutputHead + result.StandardOutputTail, StringComparison.Ordinal);
            Assert.Equal(injectFault, result.OutputOverflowed);
            Assert.Equal(injectFault ? ProcessTerminationReason.BufferOverflow : ProcessTerminationReason.None,
                result.TerminationReason);
            if (injectFault)
            {
                if (failOpening) Assert.True(actualOpenFailure);
                else
                {
                    Assert.NotNull(faulting);
                    Assert.True(faulting.FailedAfterActualFileOperation);
                }
                Assert.Single(logs.Warnings);
                Assert.Null(logs.Warnings.Single().Exception);
                Assert.DoesNotContain(checkout.Root, logs.Warnings.Single().Message, StringComparison.Ordinal);
                Assert.DoesNotContain(data.Root, logs.Warnings.Single().Message, StringComparison.Ordinal);
                Assert.DoesNotContain(marker, logs.Warnings.Single().Message, StringComparison.Ordinal);
                Assert.DoesNotContain("synthetic-owned-file-fault", logs.Warnings.Single().Message, StringComparison.Ordinal);
                if (!failOpening)
                    Assert.True(new FileInfo(stderr ? result.StandardErrorLogPath : result.StandardOutputLogPath).Length > 0);
            }
            else Assert.Empty(logs.Warnings);
        }
        finally
        {
            cancel.Cancel();
            if (ownedProcess is null && associated.Task.IsCompletedSuccessfully) ownedProcess = await associated.Task;
            if (ownedProcess is not null)
            {
                if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true);
                await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            try { await execution.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                await supervisor.WaitForStartupCleanupAsync(specification.ExecutionId).WaitAsync(TimeSpan.FromSeconds(15));
                ownedProcess?.Dispose();
            }
        }
    }

    private sealed class CountingProgress(ProcessStreamKind kind) : IProgress<ProcessOutputEvent>
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);
        public void Report(ProcessOutputEvent value)
        {
            if (value.StreamKind == kind) Interlocked.Add(ref _bytes, value.BytesCount);
        }
    }

    private sealed class WarningLog : ILogger<ProcessSupervisor>
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Enqueue((formatter(state, exception), exception));
        }
    }

    // The failure follows a real operation on a uniquely owned file, rather than preventing open.
    // A permanently failed sink also makes implicit StreamWriter disposal retry observable.
    private sealed class FaultingOwnedFile(FileStream file, bool failFlush) : Stream
    {
        public bool FailedAfterActualFileOperation { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (FailedAfterActualFileOperation) throw new IOException("synthetic-owned-file-fault");
            await file.WriteAsync(buffer, token);
            if (!failFlush)
            {
                await file.FlushAsync(token);
                FailedAfterActualFileOperation = true;
                throw new IOException("synthetic-owned-file-fault");
            }
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            if (FailedAfterActualFileOperation) throw new IOException("synthetic-owned-file-fault");
            await file.FlushAsync(token);
            FailedAfterActualFileOperation = true;
            throw new IOException("synthetic-owned-file-fault");
        }
        public override void Flush() => file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => file.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await file.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
