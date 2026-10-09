using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Legacy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// End-to-end evidence for Milestone 10B: a declared legacy entrypoint runs through the real
/// <see cref="ProcessSupervisor"/> inside an isolated scratch copy, under the real
/// <see cref="CheckoutLockService"/>, and the writer lock is always released afterwards.
/// </summary>
public sealed partial class SupervisedLegacyRunnerIntegrationTests : IDisposable
{
    private const string LegacyExecutionId = "legacy-run-1";

    private readonly TestDatabase _database = new();
    private readonly TestDirectory _appData = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly ScratchWorkspaceManager _scratchWorkspaceManager;
    private readonly SqliteProjectLockRepository _lockRepository;
    private readonly ApplicationInstanceGuard _instanceGuard;
    private readonly CheckoutLockService _checkoutLockService;
    private readonly ProcessSupervisor _processSupervisor;
    private readonly SupervisedLegacyWorkflowRunner _runner;
    private readonly string _checkoutPath;

    public SupervisedLegacyRunnerIntegrationTests()
    {
        _blobStore = new WorkflowBlobStore(_appData.Root);
        _checkoutPath = _database.GetWorkspacePath();

        Directory.CreateDirectory(_checkoutPath);

        _scratchWorkspaceManager = new ScratchWorkspaceManager(
            _blobStore,
            new SafeArchiveValidator(),
            projectRootDirectory: _checkoutPath);

        _lockRepository = new SqliteProjectLockRepository(_database.Factory);
        _instanceGuard = new ApplicationInstanceGuard(_appData.Root);
        _checkoutLockService = new CheckoutLockService(_lockRepository, _instanceGuard, TimeProvider.System);

        _processSupervisor = new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = _appData.Root });

        _runner = new SupervisedLegacyWorkflowRunner(
            _checkoutLockService,
            _scratchWorkspaceManager,
            _processSupervisor);
    }

    public void Dispose()
    {
        _instanceGuard.Dispose();
        _database.Dispose();
        _appData.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_BatchEntrypoint_RunsInScratchUnderWriterLockAndReleasesEverything()
    {
        await InitializeDatabaseAsync();

        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(
            archive,
            "run.cmd",
            "@echo off\r\necho legacy-batch-output\r\necho artifact>legacy-artifact.txt\r\nexit /b 0\r\n"));

        var request = CreateRequest(
            blob.BlobId,
            new WorkflowEntrypointDescriptor("run.cmd", WorkflowEntrypointKind.Batch, IsDeclared: true));

        var result = await _runner.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(LegacyProcessStates.Exited, result.ProcessState);
        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.NotNull(result.ProcessId);
        Assert.True(result.SourceBlobHashMatches);

        Assert.Contains("legacy-batch-output", await File.ReadAllTextAsync(result.StandardOutputLogPath));
        Assert.True(File.Exists(result.StandardErrorLogPath));
        Assert.Contains("legacy-artifact.txt", result.DiscoveredArtifactPaths);
        Assert.Contains("run.cmd", result.DiscoveredArtifactPaths);

        await AssertRunIsFullyReleasedAsync(result.ExecutionId, blob.BlobId);
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellEntrypoint_CapturesLogsAndArtifacts()
    {
        await InitializeDatabaseAsync();

        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(
            archive,
            "run.ps1",
            "Write-Output 'legacy-ps-output'; Set-Content -Path 'ps-artifact.txt' -Value 'ok'; exit 0"));

        var request = CreateRequest(
            blob.BlobId,
            new WorkflowEntrypointDescriptor("run.ps1", WorkflowEntrypointKind.PowerShell, IsDeclared: true));

        var result = await _runner.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(LegacyProcessStates.Exited, result.ProcessState);
        Assert.Contains("legacy-ps-output", await File.ReadAllTextAsync(result.StandardOutputLogPath));
        Assert.Contains("ps-artifact.txt", result.DiscoveredArtifactPaths);

        await AssertRunIsFullyReleasedAsync(result.ExecutionId, blob.BlobId);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_IsAnExecutionFailureSeparateFromWorkflowOutcome()
    {
        await InitializeDatabaseAsync();

        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(
            archive,
            "fail.cmd",
            "@echo off\r\nexit /b 7\r\n"));

        var request = CreateRequest(
            blob.BlobId,
            new WorkflowEntrypointDescriptor("fail.cmd", WorkflowEntrypointKind.Batch, IsDeclared: true),
            workflowRunId: "run-77");

        var result = await _runner.ExecuteAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(LegacyProcessStates.Exited, result.ProcessState);
        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal("run-77", result.WorkflowRunId);
        Assert.True(result.SourceBlobHashMatches);
        Assert.Equal(ExecutionState.Failed, result.ToObservableRunProjection().State);

        await AssertRunIsFullyReleasedAsync(result.ExecutionId, blob.BlobId);
    }

    [Fact]
    public async Task ExecuteAsync_MissingDeclaredEntrypoint_ReleasesLockAndRemovesScratch()
    {
        await InitializeDatabaseAsync();

        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(
            archive,
            "run.cmd",
            "@echo off\r\nexit /b 0\r\n"));

        var request = CreateRequest(
            blob.BlobId,
            new WorkflowEntrypointDescriptor(
                "scripts/missing.cmd",
                WorkflowEntrypointKind.Batch,
                IsDeclared: true));

        await Assert.ThrowsAsync<WorkflowValidationException>(() => _runner.ExecuteAsync(request));

        Assert.Null(await _lockRepository.GetActiveByRootPathAsync(_checkoutPath));

        var scratchRunDirectory = Path.Combine(_appData.Root, "scratch", "run");

        Assert.True(
            !Directory.Exists(scratchRunDirectory) || Directory.GetDirectories(scratchRunDirectory).Length == 0);
    }

    [Fact]
    public void AddWorkflowServices_RegistersSupervisedLegacyRunnerAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_appData.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var runner = provider.GetRequiredService<ISupervisedLegacyWorkflowRunner>();

        Assert.IsType<SupervisedLegacyWorkflowRunner>(runner);
        Assert.Same(runner, provider.GetRequiredService<ISupervisedLegacyWorkflowRunner>());
    }

    private async Task InitializeDatabaseAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync(projectRootPath: _checkoutPath);
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync(LegacyExecutionId);
    }

    [Fact]
    public Task PendingNativeStartupRetainsRealSqlLockAndScratchUntilCleanupCompletes() =>
        // Coordinate the live native-start barrier outside xUnit's queued synchronization context.
        Task.Run(VerifyPendingNativeStartupAsync);

    private async Task VerifyPendingNativeStartupAsync()
    {
        await InitializeDatabaseAsync();
        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Start-Sleep -Seconds 60"));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        System.Diagnostics.Process? owned = null;
        string? actualScratch = null;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
            { StartupTimeout = TimeSpan.FromMinutes(1), StartupGraceWindow = TimeSpan.FromMilliseconds(100) }),
            new StorageOptions { AppDataDirectory = _appData.Root }, null, null, process =>
            {
                actualScratch = process.StartInfo.WorkingDirectory;
                var started = process.Start();
                owned = System.Diagnostics.Process.GetProcessById(process.Id);
                _ = owned.SafeHandle;
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Legacy startup barrier expired.");
                return started;
            });
        var runner = new SupervisedLegacyWorkflowRunner(_checkoutLockService, _scratchWorkspaceManager, supervisor);
        using var cancellation = new CancellationTokenSource();
        var request = CreateRequest(blob.BlobId, new WorkflowEntrypointDescriptor("run.ps1", WorkflowEntrypointKind.PowerShell, IsDeclared: true));
        var running = runner.ExecuteAsync(request, cancellationToken: cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(LegacyProcessStates.StartupPending, result.ProcessState);
            Assert.Equal(ExecutionState.Ambiguous, result.ToObservableRunProjection().State);
            Assert.False(result.IsSuccess);
            Assert.Empty(result.DiscoveredArtifactPaths);
            Assert.NotNull(await _lockRepository.GetActiveByRootPathAsync(_checkoutPath));
            Assert.NotNull(actualScratch);
            Assert.True(File.Exists(Path.Combine(actualScratch, "run.ps1")));
            Assert.False(owned!.HasExited);
            resume.Set();
            await supervisor.WaitForStartupCleanupAsync(LegacyExecutionId).WaitAsync(TimeSpan.FromSeconds(5));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (await _lockRepository.GetActiveByRootPathAsync(_checkoutPath) is not null && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Assert.True(owned.HasExited);
            Assert.False(Directory.Exists(actualScratch));
            await AssertRunIsFullyReleasedAsync(LegacyExecutionId, blob.BlobId);
        }
        finally
        {
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

    private async Task AssertRunIsFullyReleasedAsync(string executionId, string blobId)
    {
        Assert.Null(await _lockRepository.GetActiveByRootPathAsync(_checkoutPath));

        var scratchRoot = Path.Combine(_appData.Root, "scratch", "run");
        Assert.True(!Directory.Exists(scratchRoot) || Directory.GetDirectories(scratchRoot, executionId + "-*").Length == 0);

        Assert.True(await _blobStore.VerifyBlobAsync(blobId));
    }

    private async Task<WorkflowBlob> StoreArchiveAsync(Action<System.IO.Compression.ZipArchive> configure)
    {
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(configure);

        return await _blobStore.SaveBlobAsync(new MemoryStream(archiveBytes));
    }

    private LegacyWorkflowExecutionRequest CreateRequest(
        string blobId,
        WorkflowEntrypointDescriptor entrypoint,
        string? workflowRunId = null) =>
        new(
            "project-1",
            _checkoutPath,
            "package-1",
            "version-1",
            blobId,
            entrypoint,
            workflowRunId,
            executionId: LegacyExecutionId);
}
