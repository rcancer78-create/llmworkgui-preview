using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyServerManagerReviewTests
{
    [Fact]
    public async Task PrelaunchConfigFailureDeletesOnlyTheNewGeneratedSecret()
    {
        using var data = new TestDirectory();
        var secrets = new MemorySecrets();
        secrets.Values.Add("urn:llmworkgui:secret:unrelated", "synthetic unrelated key");
        var supervisor = new SettledSupervisor();
        var manager = Manager(data.Root, supervisor, secrets);
        var run = Path.Combine(data.Root, "runs", "proxy-prelaunch-review");
        Directory.CreateDirectory(run);
        var stale = Path.Combine(run, ".env");
        await File.WriteAllTextAsync(stale, "synthetic stale payload");
        using var locked = File.Open(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => manager.StartServerAsync(new StarCliProxyServerStartRequest
            { ExecutionId = "proxy-prelaunch-review" }));

        Assert.Equal(1, secrets.SaveCount);
        Assert.Equal(0, supervisor.StartCount);
        Assert.Equal("urn:llmworkgui:secret:unrelated", Assert.Single(secrets.Values).Key);
        Assert.Single(secrets.Deleted);
    }

    [Fact]
    public async Task ConfirmedStopDeletesOwnedGeneratedReferenceExactlyOnce()
    {
        using var data = new TestDirectory();
        var secrets = new MemorySecrets();
        var supervisor = new SettledSupervisor();
        var manager = Manager(data.Root, supervisor, secrets);
        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
            { ExecutionId = "proxy-secret-stop-review" });
        Assert.Single(secrets.Values);

        await manager.StopServerAsync(instance);
        await manager.StopServerAsync(instance);

        Assert.True(supervisor.Stopped);
        Assert.False(instance.IsAlive);
        Assert.Empty(secrets.Values);
        Assert.Single(secrets.Deleted);
    }

    [Fact]
    public async Task ConfirmedStopDoesNotDeleteCallerProvisionedSharedReference()
    {
        using var data = new TestDirectory();
        var secrets = new MemorySecrets();
        const string reference = "urn:llmworkgui:secret:shared-proxy";
        secrets.Values.Add(reference, "synthetic shared key");
        var manager = Manager(data.Root, new SettledSupervisor(), secrets,
            new StarCliProxyOptions { ApiKeySecretReference = reference });
        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
            { ExecutionId = "proxy-shared-review" });

        await manager.StopServerAsync(instance);

        Assert.Equal(0, secrets.SaveCount);
        Assert.Equal(reference, Assert.Single(secrets.Values).Key);
        Assert.Empty(secrets.Deleted);
    }

    [Fact]
    public async Task UncertainPhysicalStopIsBoundedAndRemainsTrackedForRetry()
    {
        using var data = new TestDirectory();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTermination = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = data.Root }, null, null, process =>
            {
                var success = process.Start();
                var independent = Process.GetProcessById(process.Id);
                _ = independent.SafeHandle;
                started.TrySetResult(independent);
                return success;
            }, async process =>
            {
                terminationEntered.TrySetResult();
                await releaseTermination.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            });
        var options = new StarCliProxyOptions
        {
            DisposeTimeout = TimeSpan.FromMilliseconds(150),
            StartupTimeout = TimeSpan.FromSeconds(5),
            StartArguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"]
        };
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var manager = Manager(data.Root, supervisor, null, options,
            new HealthyClient(async token =>
            {
                await started.Task.WaitAsync(token);
                // Both spools are created only after native startup hands over to supervision.
                // Do not cancel at the earlier native-association barrier instead.
                var run = Path.Combine(data.Root, "runs", "proxy-physical-stop-review");
                while (!File.Exists(Path.Combine(run, "stdout.log")) || !File.Exists(Path.Combine(run, "stderr.log")))
                    await Task.Delay(10, token);
            }), executable);
        IStarCliProxyServerInstance? instance = null;
        Process? owned = null;
        const string execution = "proxy-physical-stop-review";
        try
        {
            instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest { ExecutionId = execution });
            owned = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstStop = manager.StopServerAsync(instance);
            await terminationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstFailure = await Record.ExceptionAsync(() => firstStop.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(firstFailure);
            Assert.False(owned.HasExited);
            Assert.True(instance.IsAlive, "Cancellation intent is not physical process termination.");
            var repeatedFailure = await Record.ExceptionAsync(() => manager.StopServerAsync(instance)
                .WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(repeatedFailure);
            Assert.False(owned.HasExited);

            releaseTermination.TrySetResult();
            await supervisor.WaitForStartupCleanupAsync(execution).WaitAsync(TimeSpan.FromSeconds(10));
            await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(owned.HasExited);
            Assert.False(instance.IsAlive);
        }
        finally
        {
            releaseTermination.TrySetResult();
            if (owned is null && started.Task.IsCompletedSuccessfully) owned = await started.Task;
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
            await supervisor.WaitForStartupCleanupAsync(execution).WaitAsync(TimeSpan.FromSeconds(10));
            if (instance is not null) await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static StarCliProxyServerManager Manager(string root, IProcessSupervisor supervisor, ISecretStore? secrets,
        StarCliProxyOptions? options = null, IStarCliProxyClient? client = null, string? executable = null) => new(
        Options.Create(options ?? new StarCliProxyOptions()), new Resolver(executable ?? @"C:\synthetic\proxy.exe"),
        supervisor, client ?? new HealthyClient(), new StorageOptions { AppDataDirectory = root }, secrets);

    private sealed class Resolver(string executable) : IStarCliProxyExecutableResolver
    {
        public StarCliProxyExecutableResolution Resolve() => StarCliProxyExecutableResolution.Found(executable);
    }

    private sealed class HealthyClient(Func<CancellationToken, Task>? wait = null) : IStarCliProxyClient
    {
        public async Task<StarCliProxyHealthStatus> CheckHealthAsync(StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            if (wait is not null) await wait(cancellationToken);
            return StarCliProxyHealthStatus.Healthy(1, DateTimeOffset.UtcNow);
        }
        public Task<IReadOnlyList<StarCliProxyModelInfo>> ListModelsAsync(StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StarCliProxyStreamEvent> StreamChatCompletionAsync(StarCliProxyEndpoint endpoint,
            StarCliProxyChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public int SaveCount { get; private set; }
        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = "urn:llmworkgui:secret:generated-review-" + ++SaveCount;
            Values.Add(reference, secret);
            return Task.FromResult(reference);
        }
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(reference));
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default)
        {
            Deleted.Add(reference);
            return Task.FromResult(Values.Remove(reference));
        }
    }

    private sealed class SettledSupervisor : IProcessSupervisor
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount { get; private set; }
        public bool Stopped { get; private set; }
        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null, CancellationToken cancellationToken = default)
        {
            StartCount++;
            cancellationToken.Register(() =>
            {
                Stopped = true;
                _completion.TrySetResult(new ProcessExecutionResult
                {
                    ExecutionId = specification.ExecutionId, TerminationReason = ProcessTerminationReason.UserCancelled,
                    ExitCode = 0, StartedAtUtc = DateTimeOffset.UtcNow, ExitedAtUtc = DateTimeOffset.UtcNow,
                    RunDirectory = "synthetic", StandardOutputLogPath = "synthetic", StandardErrorLogPath = "synthetic",
                    StandardOutputBytes = 0, StandardErrorBytes = 0, StandardOutputHead = "", StandardOutputTail = "",
                    StandardErrorHead = "", StandardErrorTail = "", OutputOverflowed = false
                });
            });
            return _completion.Task;
        }
    }
}
