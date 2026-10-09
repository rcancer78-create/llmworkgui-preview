using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Legacy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class SupervisedLegacyRunnerIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_BatchArgumentCannotExecuteAnAdditionalShellCommand(bool environmentExpansion)
    {
        await InitializeDatabaseAsync();
        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(archive, "run.cmd",
            "@echo off\r\necho declared-script-only\r\nexit /b 0\r\n"));
        var argument = environmentExpansion ? "%LEGACY_ARG_PAYLOAD%" : "x\" & echo injected>injected.txt & rem \"";
        var request = new LegacyWorkflowExecutionRequest("project-1", _checkoutPath, "package-1", "version-1",
            blob.BlobId, new WorkflowEntrypointDescriptor("run.cmd", WorkflowEntrypointKind.Batch, true),
            arguments: new[] { argument }, environmentVariables: new Dictionary<string, string>
            {
                ["LEGACY_ARG_PAYLOAD"] = "& echo injected>injected.txt & rem"
            }, executionId: LegacyExecutionId);

        // These grammar vectors are deliberately unsupported. Do not accept success merely because
        // artifact discovery or scratch cleanup hid the extra command's file.
        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(() => _runner.ExecuteAsync(request));
        Assert.Contains("unsupported shell grammar", refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_checkoutPath, "injected.txt")));
        Assert.Empty(Directory.EnumerateFiles(_appData.Root, "injected.txt", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(AppDataPaths.GetRunDirectory(_appData.Root, LegacyExecutionId)));
        await AssertRunIsFullyReleasedAsync(LegacyExecutionId, blob.BlobId);
    }

    [Fact]
    public async Task Review_BatchSimpleLiteralArgumentStillReachesTheDeclaredScript()
    {
        await InitializeDatabaseAsync();
        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(archive, "run.cmd",
            "@echo off\r\necho argument:%~1\r\nexit /b 0\r\n"));
        var request = new LegacyWorkflowExecutionRequest("project-1", _checkoutPath, "package-1", "version-1",
            blob.BlobId, new WorkflowEntrypointDescriptor("run.cmd", WorkflowEntrypointKind.Batch, true),
            arguments: new[] { "ordinary-argument" }, executionId: LegacyExecutionId);

        var result = await _runner.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Contains("argument:ordinary-argument", await File.ReadAllTextAsync(result.StandardOutputLogPath));
        await AssertRunIsFullyReleasedAsync(LegacyExecutionId, blob.BlobId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Review_ConfirmedLateNativeCleanupRetriesOwnedResourceFailure(bool releaseFailure) =>
        Task.Run(() => VerifyConfirmedCleanupRetryAsync(releaseFailure));

    private async Task VerifyConfirmedCleanupRetryAsync(bool releaseFailure)
    {
        await InitializeDatabaseAsync();
        var blob = await StoreArchiveAsync(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Start-Sleep -Seconds 60");
            WorkflowTestArchiveFactory.AddEntry(archive, "held.txt", "test-owned file");
        });
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        System.Diagnostics.Process? owned = null;
        FileStream? held = null;
        string? actualScratch = null;
        var logger = new CleanupWarningObserver();
        var locks = releaseFailure ? new FailOnceReleaseLockService(_checkoutLockService) : null;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
            { StartupTimeout = TimeSpan.FromMinutes(1), StartupGraceWindow = TimeSpan.FromMilliseconds(100) }),
            new StorageOptions { AppDataDirectory = _appData.Root }, null, null, process =>
            {
                actualScratch = process.StartInfo.WorkingDirectory;
                var started = process.Start();
                owned = System.Diagnostics.Process.GetProcessById(process.Id);
                _ = owned.SafeHandle;
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Legacy startup test barrier expired.");
                return started;
            });
        var runner = new SupervisedLegacyWorkflowRunner(locks ?? (ICheckoutLockService)_checkoutLockService,
            _scratchWorkspaceManager, supervisor, logger: logger);
        using var cancellation = new CancellationTokenSource();
        var running = runner.ExecuteAsync(CreateRequest(blob.BlobId,
            new WorkflowEntrypointDescriptor("run.ps1", WorkflowEntrypointKind.PowerShell, true)),
            cancellationToken: cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if (!releaseFailure)
                held = new FileStream(Path.Combine(actualScratch!, "held.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionState.Ambiguous, result.ToObservableRunProjection().State);
            Assert.Null(result.ToObservableRunProjection().EndedAtUtc);
            Assert.False(owned!.HasExited);
            Assert.NotNull(await _lockRepository.GetActiveByRootPathAsync(_checkoutPath));
            Assert.True(Directory.Exists(actualScratch));

            resume.Set();
            await supervisor.WaitForStartupCleanupAsync(LegacyExecutionId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owned.HasExited); // Independently observed native completion precedes all repair.
            await logger.FirstWarning.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await _lockRepository.GetActiveByRootPathAsync(_checkoutPath));
            held?.Dispose();
            held = null;
            locks?.AllowRetry();

            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (await _lockRepository.GetActiveByRootPathAsync(_checkoutPath) is not null && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            await AssertRunIsFullyReleasedAsync(LegacyExecutionId, blob.BlobId);
            if (releaseFailure) Assert.Equal(2, locks!.ReleaseAttempts);
        }
        finally
        {
            held?.Dispose();
            locks?.AllowRetry();
            resume.Set();
            cancellation.Cancel();
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
        }
    }

    private sealed class CleanupWarningObserver : ILogger<SupervisedLegacyWorkflowRunner>
    {
        private readonly TaskCompletionSource _warning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstWarning => _warning.Task;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) _warning.TrySetResult();
        }
    }

    private sealed class FailOnceReleaseLockService(ICheckoutLockService inner) : ICheckoutLockService
    {
        private int _releaseAttempts;
        private readonly TaskCompletionSource _releaseAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseAttempts => Volatile.Read(ref _releaseAttempts);
        public void AllowRetry() => _releaseAllowed.TrySetResult();
        public bool RequiresWriterLock(string? executionMode) => inner.RequiresWriterLock(executionMode);
        public bool RequiresWriterLock(WorkflowRole role, string? executionMode) => inner.RequiresWriterLock(role, executionMode);
        public async Task<ICheckoutLockToken> AcquireWriterLockAsync(string projectId, string canonicalRootPath,
            string executionId, long processGeneration, CancellationToken cancellationToken = default) =>
            new FailOnceToken(this, await inner.AcquireWriterLockAsync(projectId, canonicalRootPath,
                executionId, processGeneration, cancellationToken));
        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string projectId, string canonicalRootPath,
            string executionId, long processGeneration, string? executionMode, WorkflowRole role = WorkflowRole.Unknown,
            CancellationToken cancellationToken = default) => inner.AcquireLockForExecutionAsync(projectId,
                canonicalRootPath, executionId, processGeneration, executionMode, role, cancellationToken);
        private sealed class FailOnceToken(FailOnceReleaseLockService owner, ICheckoutLockToken token) : ICheckoutLockToken
        {
            public string LockId => token.LockId;
            public string ProjectId => token.ProjectId;
            public string CanonicalRootPath => token.CanonicalRootPath;
            public string ExecutionId => token.ExecutionId;
            public string ApplicationInstanceId => token.ApplicationInstanceId;
            public bool IsHeld => token.IsHeld;
            public async Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner._releaseAttempts) == 1)
                    throw new IOException("Synthetic failure before the real SQLite release write.");
                await owner._releaseAllowed.Task.WaitAsync(cancellationToken);
                await token.ReleaseAsync(reason, cancellationToken);
            }
            public void Dispose() => token.Dispose();
            public ValueTask DisposeAsync() => token.DisposeAsync();
        }
    }
}
