using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Watchdogs;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessLateStartupTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public Task LateStartupKeepsHandleUntilOwnedProcessIsStopped(bool protocol, bool cancel, bool watchdog) =>
        // The barrier coordinator must run independently of xUnit's limited synchronization context:
        // queued test continuations can otherwise outlive the native worker's safety deadline.
        // Keep the actual caller deadline and all ownership assertions unchanged.
        Task.Run(() => VerifyLateStartupAsync(protocol, cancel, watchdog));

    private static async Task VerifyLateStartupAsync(bool protocol, bool cancel, bool watchdog)
    {
        using var directory = new TestDirectory();
        using var workspace = new TestDirectory();
        using var resume = new ManualResetEventSlim();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var clock = new ManualStartupClock();
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            StartupTimeout = TimeSpan.FromMinutes(1),
            StartupGraceWindow = TimeSpan.FromMilliseconds(100)
        }), new StorageOptions { AppDataDirectory = directory.Root }, clock, null, process =>
        {
            Interlocked.Increment(ref launches);
            var result = process.Start();
            // Retain an independent OS handle for verification and unconditional fixture cleanup.
            var owned = Process.GetProcessById(process.Id);
            _ = owned.SafeHandle;
            started.SetResult(owned);
            try
            {
                if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Start barrier expired.");
                return result;
            }
            finally { workerReturned.TrySetResult(); }
        });
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "late-start",
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            // No shell children; closing stdin cannot terminate this sleeping fixture.
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"],
            WorkingDirectory = workspace.Root,
            StdinPolicy = protocol ? ProcessStdinPolicy.DirectProtocolTransport : ProcessStdinPolicy.Closed
        };
        using var cancellation = new CancellationTokenSource();
        var observations = new System.Collections.Concurrent.ConcurrentQueue<ExecutionWatchdogObservation>();
        Task call = watchdog
            ? new ExecutionWatchdog(supervisor).WatchAsync(new ExecutionWatchdogRequest
                { Specification = specification, SessionConfirmationTimeout = null, TurnHardTimeout = TimeSpan.FromSeconds(10) },
                new CallbackProgress<ExecutionWatchdogObservation>(observations.Enqueue), cancellation.Token)
            : protocol
            ? supervisor.StartProtocolProcessAsync(specification, cancellation.Token)
            : supervisor.ExecuteAsync(specification, cancellationToken: cancellation.Token);
        await Task.WhenAny(started.Task, call).WaitAsync(TimeSpan.FromSeconds(10));
        if (!started.Task.IsCompleted) await call; // Preserve a fixture launch error instead of hiding it as a timeout.
        using var ownedProcess = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Admission is already held while Start is blocked, before any abandonment/cleanup row exists.
            if (protocol)
            {
                var duplicate = await Assert.ThrowsAsync<ProcessStartupPendingException>(() =>
                    supervisor.StartProtocolProcessAsync(specification));
                Assert.Equal(ProcessTerminationReason.StartupPending, duplicate.Reason);
            }
            else Assert.Equal(ProcessTerminationReason.StartupPending,
                (await supervisor.ExecuteAsync(specification)).TerminationReason);
            Assert.Equal(1, launches);
            if (cancel) cancellation.Cancel();
            else clock.Fire();
            // A failure result/exception is allowed, but returning cannot discard process ownership.
            var failure = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(call.IsCompleted, "Caller must not wait for the blocked native start forever.");
            Assert.Equal(1, supervisor.PendingStartupCount);
            Task? cleanup = null;
            if (protocol)
            {
                cleanup = Assert.IsType<ProcessStartupPendingException>(failure).CleanupCompletion;
                Assert.False(cleanup.IsCompleted);
                await Assert.ThrowsAsync<ProcessStartupPendingException>(() => supervisor.StartProtocolProcessAsync(specification));
            }
            else
            {
                Assert.Null(failure);
                if (watchdog)
                {
                    var result = await (Task<ExecutionTurnResult>)call;
                    Assert.Equal(ExecutionTurnOutcome.Ambiguous, result.Outcome);
                    Assert.False(result.ConsumesModelRetryBudget);
                    Assert.Equal(HealthErrorClass.UnknownOrAmbiguousCompletion, result.NormalizedHealthErrorClass);
                    Assert.DoesNotContain(observations, observation => observation.Kind == ExecutionWatchdogObservationKind.ProcessExited);
                }
                else Assert.Equal(ProcessTerminationReason.StartupPending, (await (Task<ProcessExecutionResult>)call).TerminationReason);
                Assert.Equal(ProcessTerminationReason.StartupPending, (await supervisor.ExecuteAsync(specification)).TerminationReason);
            }
            Assert.Equal(1, launches);
            resume.Set();
            await workerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            Assert.True(ownedProcess.HasExited, "A late started process outlived the supervisor's ownership.");
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = Stopwatch.StartNew();
            while (supervisor.PendingStartupCount != 0 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.Equal(0, supervisor.PendingStartupCount);
        }
        finally
        {
            resume.Set();
            if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true);
            await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await workerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultAfterOsCreationStillRetainsCleanup(bool protocol)
    {
        using var directory = new TestDirectory();
        using var workspace = new TestDirectory();
        Process? owned = null;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = directory.Root }, null, null, process =>
            {
                process.Start();
                owned = Process.GetProcessById(process.Id);
                _ = owned.SafeHandle;
                throw new IOException("Synthetic post-creation failure.");
            });
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "failed-after-create",
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"],
            WorkingDirectory = workspace.Root,
            StdinPolicy = protocol ? ProcessStdinPolicy.DirectProtocolTransport : ProcessStdinPolicy.Closed
        };
        try
        {
            if (protocol)
            {
                var failure = await Assert.ThrowsAsync<ProcessStartupPendingException>(() => supervisor.StartProtocolProcessAsync(specification));
                await failure.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else Assert.Equal(ProcessTerminationReason.StartupPending, (await supervisor.ExecuteAsync(specification)).TerminationReason);
            Assert.NotNull(owned);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpoolFailureAfterSuccessfulStartDrainsOutputBeforeReportingIncompleteCapture(bool watchdog)
    {
        using var directory = new TestDirectory();
        using var workspace = new TestDirectory();
        const string executionId = "failed-spool-after-start";
        // A real opening denial must preserve reader ownership/drain, then report incomplete capture.
        Directory.CreateDirectory(Path.Combine(AppDataPaths.GetRunDirectory(directory.Root, executionId),
            ProcessSupervisorOptions.StandardOutputFileName));
        Process? owned = null;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = directory.Root }, null, null, process =>
            {
                var result = process.Start();
                owned = Process.GetProcessById(process.Id);
                _ = owned.SafeHandle;
                return result;
            });
        var specification = new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('owned-post-start-capture'); exit 0"],
            WorkingDirectory = workspace.Root
        };
        try
        {
            if (watchdog)
            {
                var result = await new ExecutionWatchdog(supervisor).WatchAsync(new ExecutionWatchdogRequest
                    { Specification = specification, SessionConfirmationTimeout = null });
                Assert.Equal(ExecutionTurnOutcome.BufferOverflow, result.Outcome);
                Assert.False(result.ConsumesModelRetryBudget);
                Assert.Equal(0, result.ExitCode);
            }
            else
            {
                var result = await supervisor.ExecuteAsync(specification);
                Assert.Equal(ProcessTerminationReason.BufferOverflow, result.TerminationReason);
                Assert.True(result.OutputCaptureIncomplete);
                Assert.False(result.OutputLimitExceeded);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("owned-post-start-capture", result.StandardOutputHead + result.StandardOutputTail);
                Assert.Equal("owned-post-start-capture".Length, result.StandardOutputBytes);
            }
            Assert.NotNull(owned);
            Assert.True(owned.HasExited);
            await supervisor.WaitForStartupCleanupAsync(executionId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owned.HasExited);
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
        }
    }

    [Fact]
    public async Task ProtocolDisposalDeadlineRetainsCleanupTaskAndCanBeAwaitedAgain()
    {
        using var directory = new TestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var owned = Process.GetProcessById(process.Id);
        _ = owned.SafeHandle;
        using var tree = ProcessTreeTerminator.Create(NullLogger.Instance);
        tree.TryAssign(process);
        using var stderrCancellation = new CancellationTokenSource();
        var spool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualStartupClock();
        var released = 0;
        var session = new ProtocolProcessSession("protocol-held-spool", process, tree, directory.Root,
            directory.GetPath("stderr.log"), stderrCancellation, spool.Task, () => 0,
            DateTimeOffset.UtcNow, TimeSpan.Zero, clock, NullLogger.Instance,
            () => Interlocked.Increment(ref released));
        try
        {
            var disposing = session.DisposeAsync().AsTask();
            clock.Fire(); // Expire only the caller's injected 45s deadline, not the cleanup operation.
            var pending = await Assert.ThrowsAsync<ProcessStartupPendingException>(() => disposing);
            Assert.Equal(ProcessTerminationReason.CleanupPending, pending.Reason);
            Assert.False(pending.CleanupCompletion.IsCompleted);
            Assert.Equal(0, released);
            spool.SetResult();
            await pending.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            await session.DisposeAsync();
            Assert.Equal(1, released);
            Assert.True(owned.HasExited);
        }
        finally
        {
            spool.TrySetResult();
            if (!owned.HasExited) owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnconfirmedTerminationLeavesNormalDrainAndEntersRetainedCleanup()
    {
        using var data = new TestDirectory();
        using var workspace = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = data.Root }, null, null, process =>
            {
                var success = process.Start();
                var handle = Process.GetProcessById(process.Id);
                _ = handle.SafeHandle;
                started.SetResult(handle);
                return success;
            }, _ => Task.FromException(new TimeoutException("Synthetic unconfirmed tree termination.")));
        var execution = supervisor.ExecuteAsync(new ProcessStartSpecification
        {
            ExecutionId = "unconfirmed-stop", WorkingDirectory = workspace.Root,
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"]
        }, cancellationToken: cancellation.Token);
        using var owned = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            cancellation.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProcessTerminationReason.CleanupPending, result.TerminationReason);
            Assert.Null(result.ExitCode);
            await supervisor.WaitForStartupCleanupAsync("unconfirmed-stop").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owned.HasExited);
        }
        finally
        {
            if (!owned.HasExited) owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorCleanupDeadlineDoesNotPoisonSharedDisposal(bool stopFirst)
    {
        var protocol = new HeldProtocol();
        var clock = new ManualStartupClock();
        var session = new CursorAcpProcessSession("held-protocol", 1, "unused", "unused", protocol, null,
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), NullLogger.Instance, clock);
        try
        {
            var first = stopFirst ? session.StopAsync() : session.DisposeAsync().AsTask();
            clock.Fire();
            var pending = await Assert.ThrowsAsync<ProcessStartupPendingException>(() => first);
            Assert.Equal(ProcessTerminationReason.CleanupPending, pending.Reason);
            Assert.False(pending.CleanupCompletion.IsCompleted);
            Assert.Equal(0, protocol.DisposeCount);
            var retry = session.DisposeAsync().AsTask();
            protocol.Release();
            await retry.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
            Assert.Equal(1, protocol.DisposeCount);
        }
        finally { protocol.Release(); await session.DisposeAsync(); }
    }

    [Fact]
    public async Task CursorTransportFailureRetainsUnpublishedProtocolUntilCleanupCompletes()
    {
        using var data = new TestDirectory();
        var protocol = new HeldProtocol();
        var supervisor = new HeldProtocolSupervisor(protocol);
        var clock = new ManualStartupClock();
        var manager = new CursorAcpProcessManager(
            CursorAcp.StubCursorExecutableResolver.Found("unused.exe"), supervisor,
            new StorageOptions { AppDataDirectory = data.Root },
            Options.Create(new CursorAcpOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) }),
            new ThrowingTransportFactory(), timeProvider: clock);
        try
        {
            var starting = manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = protocol.ExecutionId });
            clock.Fire();
            var result = await starting;
            Assert.True(result.RequiresReconciliation);
            Assert.Null(result.Session);
            var repeated = manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = protocol.ExecutionId });
            clock.Fire();
            Assert.True((await repeated).RequiresReconciliation);
            Assert.Equal(1, supervisor.Launches);
            Assert.True(protocol.IsRunning);
        }
        finally { protocol.Release(); await protocol.DisposeAsync(); }
    }

    [Theory]
    [InlineData("zero-exit")]
    [InlineData("unknown-exit")]
    [InlineData("pending-spool")]
    public async Task AlreadyExitedProtocolIsNeverPublished(string scenario)
    {
        using var data = new TestDirectory();
        var protocol = new HeldProtocol { RootExited = true };
        if (scenario != "pending-spool") protocol.Release(scenario == "zero-exit" ? 0 : null);
        var clock = new ManualStartupClock();
        var supervisor = new HeldProtocolSupervisor(protocol);
        var transport = new CountingTransportFactory();
        var manager = new CursorAcpProcessManager(
            CursorAcp.StubCursorExecutableResolver.Found("unused.exe"), supervisor,
            new StorageOptions { AppDataDirectory = data.Root },
            Options.Create(new CursorAcpOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) }),
            transport, timeProvider: clock);
        try
        {
            var starting = manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = protocol.ExecutionId });
            if (scenario == "pending-spool")
            {
                var cleanupTimer = clock.NextTimer;
                clock.Fire();
                await cleanupTimer.WaitAsync(TimeSpan.FromSeconds(5));
                clock.Fire();
            }
            var result = await starting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result.Session);
            Assert.Equal(0, transport.Calls);
            Assert.Equal(scenario == "pending-spool" ? CursorAcpProcessStartFailureKind.ProcessCleanupPending
                : CursorAcpProcessStartFailureKind.LaunchFailed, result.FailureKind);
            if (scenario == "pending-spool")
            {
                var repeated = manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = protocol.ExecutionId });
                clock.Fire();
                Assert.True((await repeated).RequiresReconciliation);
                Assert.Equal(1, supervisor.Launches);
            }
            else Assert.Equal(1, protocol.DisposeCount);
        }
        finally { protocol.Release(); await protocol.DisposeAsync(); }
    }

    private sealed class CountingTransportFactory : IJsonRpcTransportFactory
    {
        public int Calls { get; private set; }
        public IJsonRpcTransport Create(Stream agentStandardOutput, Stream agentStandardInput)
        {
            Calls++;
            throw new IOException("A dead protocol must never be bound");
        }
    }

    private sealed class ThrowingTransportFactory : IJsonRpcTransportFactory
    {
        public IJsonRpcTransport Create(Stream agentStandardOutput, Stream agentStandardInput) => throw new IOException("Transport construction failed.");
    }

    private sealed class HeldProtocolSupervisor(HeldProtocol protocol) : IProcessSupervisor
    {
        public int Launches { get; private set; }
        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IProtocolProcessSession> StartProtocolProcessAsync(ProcessStartSpecification specification, CancellationToken cancellationToken = default)
        {
            Launches++;
            return Task.FromResult<IProtocolProcessSession>(protocol);
        }
    }

    private sealed class HeldProtocol : IProtocolProcessSession
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ExecutionId => "held-protocol";
        public int ProcessId => 1;
        public string RunDirectory => "unused";
        public string StandardErrorLogPath => "unused";
        public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream();
        public bool RootExited { get; init; }
        public bool IsRunning => !RootExited && !Completion.IsCompleted;
        public Task<ProcessExecutionResult> Completion => _completion.Task;
        public int DisposeCount { get; private set; }
        private int _disposed;
        public async Task StopAsync(CancellationToken cancellationToken = default) => await Completion;
        public async ValueTask DisposeAsync()
        {
            await Completion;
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DisposeCount++; StandardInput.Dispose(); StandardOutput.Dispose();
        }
        public void Release(int? exitCode = 0) => _completion.TrySetResult(new ProcessExecutionResult
        {
            ExecutionId = ExecutionId, StartedAtUtc = StartedAtUtc, ExitedAtUtc = DateTimeOffset.UtcNow,
            RunDirectory = "unused", StandardOutputLogPath = "unused", StandardErrorLogPath = "unused",
            StandardOutputHead = "", StandardOutputTail = "", StandardErrorHead = "", StandardErrorTail = "",
            StandardOutputBytes = 0, StandardErrorBytes = 0, OutputOverflowed = false,
            ExitCode = exitCode, TerminationReason = ProcessTerminationReason.UserCancelled
        });
    }

    private sealed class ManualStartupClock : TimeProvider
    {
        private Action? _fire;
        private TaskCompletionSource _nextTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task NextTimer => _nextTimer.Task;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualStartupTimer();
            _fire = () => { if (!timer.Disposed) callback(state); };
            var armed = _nextTimer;
            _nextTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            armed.TrySetResult();
            return timer;
        }
        public void Fire() => (_fire ?? throw new InvalidOperationException("No startup deadline was armed."))();
        private sealed class ManualStartupTimer : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
