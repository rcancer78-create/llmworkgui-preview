using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_VersionProbe_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-discovery";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            using var directory = new TestDirectory();
            var executablePath = directory.GetPath("opencode.cmd");
            File.WriteAllText(executablePath, "@echo off\r\n");
            var supervisor = new RecordingProbeSupervisor();
            var service = new OpenCodeDiscoveryService(supervisor, new[] { directory.Root });

            var result = await service.DiscoverAsync();

            Assert.True(result.IsSupportedVersion);
            var specification = supervisor.Specification;
            Assert.NotNull(specification);
            Assert.False(specification!.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(directory.Root, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            var parentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Assert.DoesNotContain(parentPath, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WhenCliReportsSupportedVersion_ReturnsSupportedResult()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "1.18.31");
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.True(result.IsSupportedVersion);
        Assert.Equal("1.18.31", result.Version);
        Assert.Equal(directory.GetPath("opencode.cmd"), result.ExecutablePath);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task DiscoverAsync_WhenVersionHasPrefix_ParsesVersion()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "opencode 1.19.2");
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.True(result.IsSupportedVersion);
        Assert.Equal("1.19.2", result.Version);
    }

    [Fact]
    public async Task DiscoverAsync_WhenVersionIsBelowBaseline_ReturnsUnsupportedResult()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "1.17.9");
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Equal("1.17.9", result.Version);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("1.18.31", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_WhenVersionOutputIsInvalid_ReturnsUnsupportedResult()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "not-a-version");
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Null(result.Version);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCliExitsWithNonZeroCode_ReturnsUnsupportedResult()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "1.18.31", exitCode: 3);
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("exited with code 3", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCliIsMissing_ReturnsNotInstalledWithoutThrowing()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        var service = CreateService(directory.Root, dataDirectory.Root);

        var result = await service.DiscoverAsync();

        Assert.False(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.Version);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCallerCancels_ThrowsOperationCanceled()
    {
        using var directory = new TestDirectory();
        using var dataDirectory = new TestDirectory();
        WriteFakeCli(directory, "1.18.31");
        var service = CreateService(directory.Root, dataDirectory.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task DiscoverAsync_WhenSupervisorThrowsOnProbeTimeout_ReturnsInstalledButUnsupported()
    {
        using var directory = new TestDirectory();
        WriteFakeCli(directory, "1.18.31");
        var supervisor = new CancellableProbeSupervisor();
        var service = new OpenCodeDiscoveryService(supervisor, new[] { directory.Root },
            probeTimeout: TimeSpan.FromMilliseconds(50));
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await service.DiscoverAsync(caller.Token);

        Assert.True(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Equal(directory.GetPath("opencode.cmd"), result.ExecutablePath);
        Assert.Contains("did not complete", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCallerCancelsAnActiveProbe_PropagatesCancellation()
    {
        using var directory = new TestDirectory();
        WriteFakeCli(directory, "1.18.31");
        var supervisor = new CancellableProbeSupervisor();
        var service = new OpenCodeDiscoveryService(supervisor, new[] { directory.Root });
        using var caller = new CancellationTokenSource();
        var operation = service.DiscoverAsync(caller.Token);
        try
        {
            await supervisor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally
        {
            caller.Cancel();
        }
    }

    private sealed class RecordingProbeSupervisor : IProcessSupervisor
    {
        public ProcessStartSpecification? Specification { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            Specification = specification;
            return Task.FromResult(new ProcessExecutionResult
            {
                ExecutionId = specification.ExecutionId,
                TerminationReason = ProcessTerminationReason.None,
                ExitCode = 0,
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                ExitedAtUtc = DateTimeOffset.UnixEpoch,
                RunDirectory = @"C:\runs\discovery",
                StandardOutputLogPath = @"C:\runs\discovery\stdout.log",
                StandardErrorLogPath = @"C:\runs\discovery\stderr.log",
                StandardOutputBytes = 8,
                StandardErrorBytes = 0,
                StandardOutputHead = "1.18.31",
                StandardOutputTail = "1.18.31",
                StandardErrorHead = string.Empty,
                StandardErrorTail = string.Empty,
                OutputOverflowed = false
            });
        }
    }

    private sealed class CancellableProbeSupervisor : IProcessSupervisor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The synthetic probe must be cancelled.");
        }
    }

    private static OpenCodeDiscoveryService CreateService(string searchDirectory, string appDataDirectory)
    {
        return new OpenCodeDiscoveryService(
            new ProcessSupervisor(
                Options.Create(new ProcessSupervisorOptions()),
                new StorageOptions { AppDataDirectory = appDataDirectory }),
            new[] { searchDirectory });
    }

    private static void WriteFakeCli(TestDirectory directory, string output, int exitCode = 0)
    {
        var body = new StringBuilder()
            .AppendLine("@echo off")
            .Append("echo ")
            .AppendLine(output)
            .Append("exit /b ")
            .AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));

        File.WriteAllText(
            directory.GetPath("opencode.cmd"),
            body.ToString(),
            new UTF8Encoding(false));
    }
}
