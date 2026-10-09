using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

[Collection(CursorAcpProcessCollection.Name)]
public sealed class CursorAcpProcessManagerTests
{
    [Fact]
    public async Task StartAsync_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-cursor";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            using var dataDirectory = new TestDirectory();
            var executablePath = @"C:\tools\cursor-agent.exe";
            var supervisor = new RecordingProtocolSupervisor();
            var manager = CreateManager(
                StubCursorExecutableResolver.Found(executablePath),
                supervisor,
                dataDirectory.Root);

            await manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = "exec-no-inherit" });

            var specification = supervisor.Specification;
            Assert.NotNull(specification);
            Assert.False(specification!.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetDirectoryName(executablePath)!, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task StopWithCancelledCallerStillTerminatesOwnedProcess()
    {
        using var executable = new FakeCursorAgentExecutable();
        using var dataDirectory = new TestDirectory();
        var manager = CreateManager(StubCursorExecutableResolver.Found(executable.ExecutablePath),
            CreateSupervisor(dataDirectory.Root), dataDirectory.Root);
        var result = await manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = "cancelled-stop" });
        var session = result.Session!;
        await FakeCursorAgentExecutable.WaitForFileAsync(executable.ReadyFilePath, TimeSpan.FromSeconds(60));
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await manager.StopAsync(session, cancelled.Token);
            Assert.False(session.IsRunning);
        }
        finally { await session.DisposeAsync(); }
    }
    [Fact]
    public async Task StartAsync_RealProcess_StartsAndStopsWithProcessTreeTermination()
    {
        using var executable = new FakeCursorAgentExecutable();
        using var dataDirectory = new TestDirectory();
        var manager = CreateManager(
            StubCursorExecutableResolver.Found(executable.ExecutablePath),
            CreateSupervisor(dataDirectory.Root),
            dataDirectory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-tree"
        });

        Assert.True(result.IsStarted);
        var session = result.Session!;
        Assert.True(session.IsRunning);
        Assert.Equal("exec-cursor-tree", session.ExecutionId);

        // The JSON-RPC transport is bound to the live stdio duplex channel of the managed process.
        Assert.NotNull(session.Transport);

        await FakeCursorAgentExecutable.WaitForFileAsync(executable.ReadyFilePath, TimeSpan.FromSeconds(60));

        var childProcessId = await FakeCursorAgentExecutable.WaitForPidAsync(
            executable.ChildProcessIdFilePath,
            TimeSpan.FromSeconds(60));

        Assert.True(FakeCursorAgentExecutable.IsProcessAlive(childProcessId));

        await manager.StopAsync(session);

        Assert.False(session.IsRunning);
        Assert.True(await FakeCursorAgentExecutable.WaitForProcessExitAsync(
            childProcessId,
            TimeSpan.FromSeconds(30)));

        Assert.True(Directory.Exists(session.RunDirectory));

        // Standard error is still spooled by the supervisor with a bounded writer.
        Assert.True(File.Exists(Path.Combine(session.RunDirectory, ProcessSupervisorOptions.StandardErrorFileName)));

        // Standard output is deliberately never spooled for a protocol process: it is owned by the
        // JSON-RPC transport and its frames may carry workspace content (ADR-0003 §1).
        Assert.False(File.Exists(Path.Combine(session.RunDirectory, ProcessSupervisorOptions.StandardOutputFileName)));
    }

    [Fact]
    public async Task StartAsync_NotInstalled_ReportsDegradedWithoutStartingAnyProcess()
    {
        using var dataDirectory = new TestDirectory();
        var manager = CreateManager(
            StubCursorExecutableResolver.Missing(),
            CreateSupervisor(dataDirectory.Root),
            dataDirectory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-not-installed"
        });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpProcessStartFailureKind.ExecutableMissing, result.FailureKind);
        Assert.Equal(CursorAcpPolicy.NotInstalledBlocker, result.Blocker);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Null(result.Session);
        Assert.False(Directory.Exists(Path.Combine(dataDirectory.Root, "runs", "exec-cursor-not-installed")));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheManagedProcess()
    {
        using var executable = new FakeCursorAgentExecutable();
        using var dataDirectory = new TestDirectory();
        var manager = CreateManager(
            StubCursorExecutableResolver.Found(executable.ExecutablePath),
            CreateSupervisor(dataDirectory.Root),
            dataDirectory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-dispose"
        });

        var session = result.Session!;
        await FakeCursorAgentExecutable.WaitForFileAsync(executable.ReadyFilePath, TimeSpan.FromSeconds(60));

        await session.DisposeAsync();

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        using var executable = new FakeCursorAgentExecutable();
        using var dataDirectory = new TestDirectory();
        var manager = CreateManager(
            StubCursorExecutableResolver.Found(executable.ExecutablePath),
            CreateSupervisor(dataDirectory.Root),
            dataDirectory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-stop-twice"
        });

        var session = result.Session!;
        await FakeCursorAgentExecutable.WaitForFileAsync(executable.ReadyFilePath, TimeSpan.FromSeconds(60));

        await manager.StopAsync(session);
        await manager.StopAsync(session);

        Assert.False(session.IsRunning);
    }

    private sealed class RecordingProtocolSupervisor : IProcessSupervisor
    {
        public ProcessStartSpecification? Specification { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IProtocolProcessSession> StartProtocolProcessAsync(
            ProcessStartSpecification specification,
            CancellationToken cancellationToken = default)
        {
            Specification = specification;
            throw new InvalidOperationException("captured");
        }
    }

    private static ProcessSupervisor CreateSupervisor(string appDataDirectory) =>
        new(
            Options.Create(new ProcessSupervisorOptions
            {
                GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500)
            }),
            new StorageOptions { AppDataDirectory = appDataDirectory });

    private static CursorAcpProcessManager CreateManager(
        ICursorExecutableResolver resolver,
        IProcessSupervisor supervisor,
        string appDataDirectory)
    {
        var options = Options.Create(new CursorAcpOptions { ShutdownTimeout = TimeSpan.FromSeconds(15) });

        return new CursorAcpProcessManager(
            resolver,
            supervisor,
            new StorageOptions { AppDataDirectory = appDataDirectory },
            options,
            new JsonRpcStdioTransportFactory(options));
    }
}
