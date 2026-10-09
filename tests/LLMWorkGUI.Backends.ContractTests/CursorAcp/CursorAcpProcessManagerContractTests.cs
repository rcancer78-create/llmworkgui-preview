using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpProcessManagerContractTests
{
    [Fact]
    public async Task StartAsync_ResolvedExecutable_BuildsTypedSpecificationOutsideCheckout()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-cursor";
        const string executablePath = @"C:\tools\cursor-agent.exe";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            using var directory = new CursorAcpTestDirectory();
            var supervisor = StubProcessSupervisor.Hanging();
            var manager = Create(
                supervisor,
                FakeCursorExecutableResolver.Found(executablePath, "2026.09.15-d2fe57e"),
                directory.Root);

            var result = await manager.StartAsync(new CursorAcpProcessStartRequest
            {
                ExecutionId = "exec-cursor-exe"
            });

            Assert.True(result.IsStarted);
            Assert.NotNull(result.Session);
            Assert.Equal("exec-cursor-exe", result.Session!.ExecutionId);
            Assert.Equal(73, result.Session.ProcessGeneration);
            Assert.Equal(Assert.Single(supervisor.ProtocolSessions).ProcessGeneration, result.Session.ProcessGeneration);
            Assert.Equal(Path.Combine(result.Session.RunDirectory, "workspace"), result.Session.WorkingDirectory);
            Assert.True(Directory.Exists(result.Session.WorkingDirectory));

            var specification = Assert.Single(supervisor.Specifications);
            Assert.Equal(executablePath, specification.FileName);
            Assert.Equal(new[] { "acp" }, specification.Arguments);
            Assert.Equal(ProcessStdinPolicy.DirectProtocolTransport, specification.StdinPolicy);
            Assert.False(specification.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
            Assert.Equal(baseline["SystemRoot"], specification.EnvironmentVariables["SystemRoot"]);
            Assert.Equal(baseline["windir"], specification.EnvironmentVariables["windir"]);
            Assert.Equal(baseline["ComSpec"], specification.EnvironmentVariables["ComSpec"]);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetDirectoryName(executablePath)!, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            var parentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Assert.DoesNotContain(parentPath, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);

            var workingDirectory = Path.GetFullPath(specification.WorkingDirectory!);
            Assert.StartsWith(Path.GetFullPath(directory.Root), workingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Path.Combine("runs", "exec-cursor-exe", "workspace"),
                workingDirectory,
                StringComparison.OrdinalIgnoreCase);
            await manager.StopAsync(result.Session!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task StartAsync_BatchExecutable_UsesCmdInterpreterWithSeparateArgumentTokens()
    {
        using var directory = new CursorAcpTestDirectory();
        var supervisor = StubProcessSupervisor.Hanging();
        var executablePath = @"C:\tools\cursor-agent.cmd";
        var manager = Create(
            supervisor,
            FakeCursorExecutableResolver.Found(executablePath, "1.0.0"),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-cmd"
        });

        Assert.True(result.IsStarted);

        var specification = Assert.Single(supervisor.Specifications);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), specification.FileName);
        Assert.Equal(new[] { "/d", "/c", executablePath, "acp" }, specification.Arguments);
        Assert.Equal(ProcessStdinPolicy.DirectProtocolTransport, specification.StdinPolicy);
        await manager.StopAsync(result.Session!);
    }

    [Fact]
    public async Task StartAsync_MissingExecutable_ReturnsDegradedResultWithoutSupervisorCall()
    {
        using var directory = new CursorAcpTestDirectory();
        var supervisor = StubProcessSupervisor.Returning();
        var manager = Create(supervisor, FakeCursorExecutableResolver.Missing(), directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-missing"
        });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpProcessStartFailureKind.ExecutableMissing, result.FailureKind);
        Assert.Equal(CursorAcpPolicy.NotInstalledBlocker, result.Blocker);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Null(result.Session);
        Assert.Empty(supervisor.Specifications);
    }

    [Fact]
    public async Task StartAsync_UnresolvedExecutable_ReturnsDegradedUnresolvedResult()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Returning(),
            FakeCursorExecutableResolver.Unresolved("'cursor-agent --version' timed out."),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-unresolved"
        });

        Assert.Equal(CursorAcpProcessStartFailureKind.ExecutableUnresolved, result.FailureKind);
        Assert.Contains("timed out", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_ResolverThrows_ReturnsDegradedWithoutUnhandledException()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Returning(),
            FakeCursorExecutableResolver.Throwing(new InvalidOperationException("discovery exploded")),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-discovery-failure"
        });

        Assert.Equal(CursorAcpProcessStartFailureKind.ExecutableUnresolved, result.FailureKind);
        Assert.Contains("discovery exploded", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_SupervisorRejectsLaunch_ReturnsDegradedLaunchFailed()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Throwing(new InvalidOperationException("instance guard blocked")),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-launch-failure"
        });

        Assert.Equal(CursorAcpProcessStartFailureKind.LaunchFailed, result.FailureKind);
        Assert.Contains("instance guard blocked", result.Blocker, StringComparison.Ordinal);
        Assert.Equal(CursorAcpPolicy.ProcessStartupGuidance, result.Guidance);
    }

    [Fact]
    public async Task PendingSupervisorOwnershipIsNotReportedAsLaunchFailure()
    {
        using var directory = new CursorAcpTestDirectory();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Create(
            StubProcessSupervisor.Throwing(new LLMWorkGUI.Application.Processes.ProcessStartupPendingException(cleanup.Task)),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"), directory.Root);
        var result = await manager.StartAsync(new CursorAcpProcessStartRequest { ExecutionId = "pending-native" });
        Assert.False(result.IsStarted);
        Assert.Null(result.Session);
        Assert.True(result.RequiresReconciliation);
        Assert.Equal(CursorAcpProcessStartFailureKind.ProcessCleanupPending, result.FailureKind);
        Assert.Contains("Do not retry", result.Guidance);
        cleanup.SetResult();
    }

    [Fact]
    public async Task StartAsync_InvalidExecutionId_ReturnsDegradedLaunchFailed()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Returning(),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = ".."
        });

        Assert.Equal(CursorAcpProcessStartFailureKind.LaunchFailed, result.FailureKind);
        Assert.Contains("working directory", result.Blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_SupervisorReportsStartupFailure_ReturnsDegradedLaunchFailed()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.ReturningStartupFailure("Access is denied."),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-startup-failure"
        });

        Assert.Equal(CursorAcpProcessStartFailureKind.LaunchFailed, result.FailureKind);
        Assert.Contains("Access is denied.", result.Blocker, StringComparison.Ordinal);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task StartAsync_DuplicateExecutionId_ReturnsDegradedWithoutSecondLaunch()
    {
        using var directory = new CursorAcpTestDirectory();
        var supervisor = StubProcessSupervisor.Hanging();
        var manager = Create(
            supervisor,
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var first = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-duplicate"
        });

        var second = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-duplicate"
        });

        Assert.True(first.IsStarted);
        Assert.Equal(CursorAcpProcessStartFailureKind.ProcessCleanupPending, second.FailureKind);
        Assert.True(second.RequiresReconciliation);
        Assert.Contains("already running", second.Blocker, StringComparison.Ordinal);
        Assert.Single(supervisor.Specifications);

        await manager.StopAsync(first.Session!);
    }

    [Fact]
    public async Task StartAsync_ExitedSession_ReleasesExecutionIdForRestart()
    {
        using var directory = new CursorAcpTestDirectory();
        var supervisor = StubProcessSupervisor.Hanging();
        var manager = Create(
            supervisor,
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var first = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-restart"
        });

        Assert.True(first.IsStarted);
        await Assert.Single(supervisor.ProtocolSessions).StopAsync();
        var second = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-restart"
        });

        Assert.True(first.IsStarted);
        Assert.False(first.Session!.IsRunning);
        Assert.True(second.IsStarted);
        Assert.Equal(2, supervisor.Specifications.Count);
        await manager.StopAsync(second.Session!);
    }

    [Fact]
    public async Task StopAsync_HangingProcess_CancelsLifetimeAndCompletesSession()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Hanging(),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        var result = await manager.StartAsync(new CursorAcpProcessStartRequest
        {
            ExecutionId = "exec-cursor-hanging"
        });

        Assert.True(result.IsStarted);
        Assert.True(result.Session!.IsRunning);

        await manager.StopAsync(result.Session);

        Assert.False(result.Session.IsRunning);
    }

    [Fact]
    public async Task StopAsync_ForeignSession_ThrowsArgumentException()
    {
        using var directory = new CursorAcpTestDirectory();
        var manager = Create(
            StubProcessSupervisor.Returning(),
            FakeCursorExecutableResolver.Found(@"C:\tools\cursor-agent.exe", "1.0.0"),
            directory.Root);

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.StopAsync(new ForeignCursorAcpProcessSession()));
    }

    private static CursorAcpProcessManager Create(
        StubProcessSupervisor supervisor,
        FakeCursorExecutableResolver resolver,
        string appDataDirectory) =>
        new(
            resolver,
            supervisor,
            new StorageOptions { AppDataDirectory = appDataDirectory },
            options: null,
            processIdResolver: null,
            timeProvider: null,
            logger: null);

    private sealed class ForeignCursorAcpProcessSession : ICursorAcpProcessSession
    {
        public string ExecutionId => "foreign";

        public int? ProcessId => null;

        public string WorkingDirectory => string.Empty;

        public string RunDirectory => string.Empty;

        public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;

        public bool IsRunning => false;

        public IJsonRpcTransport? Transport => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
