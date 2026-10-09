using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class HardeningDiagnosticsViewModelTests
{
    [Fact]
    public async Task PreviewFailure_DoesNotPublishExceptionPayload()
    {
        var service = new StubDiagnosticBundleService(CreatePreview(false))
        { PreviewHandler = () => throw new InvalidOperationException("private-bare-canary") };
        var model = new HardeningDiagnosticsViewModel(diagnosticBundleService: service);
        Assert.Null(await model.PreviewDiagnosticBundleAsync());
        Assert.False(model.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(model.LastError));
        Assert.DoesNotContain("private-bare-canary", model.LastError);
    }

    [Fact]
    public async Task HostStopping_CancelsAnActiveHardeningOperationThroughRealComposition()
    {
        using var lifetime = new StoppingLifetime();
        var release = new TaskCompletionSource<DiagnosticBundlePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var diagnostic = new StubDiagnosticBundleService(CreatePreview(false))
        {
            CancellablePreviewHandler = token => { observedToken = token; return release.Task; }
        };
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IDiagnosticBundleService>(diagnostic);
        services.AddUnifiedWorkspaceShell();
        using var provider = services.BuildServiceProvider();
        var viewModel = provider.GetRequiredService<HardeningDiagnosticsViewModel>();
        var operation = viewModel.PreviewDiagnosticBundleAsync();
        try
        {
            Assert.True(viewModel.IsBusy);
            lifetime.StopApplication();
            Assert.True(observedToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult(CreatePreview(false));
            await operation;
        }
    }

    private sealed class StoppingLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    [Fact]
    public async Task PendingPreview_DoesNotAdmitAnotherOperation()
    {
        var release = new TaskCompletionSource<DiagnosticBundlePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubDiagnosticBundleService(CreatePreview(blocked: false))
        { PreviewHandler = () => release.Task };
        var viewModel = new HardeningDiagnosticsViewModel(diagnosticBundleService: service);
        var first = viewModel.PreviewDiagnosticBundleAsync();
        var second = viewModel.PreviewDiagnosticBundleAsync();
        release.SetResult(CreatePreview(blocked: false));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, service.PreviewCount);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasExportablePreview);
    }

    [Fact]
    public async Task PreviewContinuation_PublishesBindableStateOnTheInvokingDispatcher()
    {
        await StaTestRunner.Run(async () =>
        {
            var dispatcherThread = Environment.CurrentManagedThreadId;
            var release = new TaskCompletionSource<DiagnosticBundlePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new StubDiagnosticBundleService(CreatePreview(blocked: false))
            { PreviewHandler = () => release.Task };
            var viewModel = new HardeningDiagnosticsViewModel(diagnosticBundleService: service);
            var wrongThread = false;
            viewModel.PropertyChanged += (_, _) =>
            {
                if (Environment.CurrentManagedThreadId != dispatcherThread) wrongThread = true;
            };
            var operation = viewModel.PreviewDiagnosticBundleAsync();
            await Task.Run(() => release.SetResult(CreatePreview(blocked: false)));
            await operation;

            Assert.False(wrongThread);
            Assert.True(viewModel.HasExportablePreview);
        });
    }

    [Fact]
    public void AddUnifiedWorkspaceShell_ComposesTheHardeningSurfaceWithoutInfrastructure()
    {
        var services = new ServiceCollection();

        services.AddApplication();
        services.AddAppUi();
        services.AddUnifiedWorkspaceShell();

        using var provider = services.BuildServiceProvider();

        var viewModel = provider.GetRequiredService<HardeningDiagnosticsViewModel>();

        Assert.False(viewModel.IsAvailable);
    }

    [Fact]
    public void WithoutServices_TheSurfaceReportsItselfUnavailable()
    {
        var viewModel = new HardeningDiagnosticsViewModel();

        Assert.False(viewModel.IsAvailable);
        Assert.False(viewModel.IsDiagnosticBundleAvailable);
        Assert.False(viewModel.IsDatabaseBackupAvailable);
        Assert.False(viewModel.IsCrashRecoveryAvailable);
        Assert.False(viewModel.IsRetentionCleanupAvailable);
        Assert.False(viewModel.IsQuotaSoakAvailable);
        Assert.False(viewModel.IsAcceptanceScenarioAvailable);
        Assert.False(viewModel.PreviewDiagnosticBundleCommand.CanExecute(null));
        Assert.False(viewModel.RunRetentionCleanupCommand.CanExecute(null));
        Assert.False(viewModel.RunQuotaSoakCommand.CanExecute(null));
        Assert.False(viewModel.RunAcceptanceScenarioCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewDiagnosticBundle_PublishesFilesAndEnablesExport()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            diagnosticBundleService: new StubDiagnosticBundleService(CreatePreview(blocked: false)));

        var preview = await viewModel.PreviewDiagnosticBundleAsync();

        Assert.NotNull(preview);
        Assert.True(viewModel.HasExportablePreview);
        Assert.False(viewModel.IsPreviewBlocked);
        Assert.Single(viewModel.PreviewFiles);
        Assert.Empty(viewModel.PreviewWarnings);
        Assert.Null(viewModel.LastError);
        Assert.Contains("Preview ready", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewDiagnosticBundle_KeepsABlockedPreviewUnavailableForExport()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            diagnosticBundleService: new StubDiagnosticBundleService(CreatePreview(blocked: true)));

        await viewModel.PreviewDiagnosticBundleAsync();

        Assert.True(viewModel.IsPreviewBlocked);
        Assert.False(viewModel.HasExportablePreview);

        var result = await viewModel.CreateDiagnosticBundleAsync();

        Assert.Null(result);
        Assert.NotNull(viewModel.LastError);
        Assert.Contains("preview", viewModel.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDiagnosticBundle_RequiresAPreview()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            diagnosticBundleService: new StubDiagnosticBundleService(CreatePreview(blocked: false)));

        var result = await viewModel.CreateDiagnosticBundleAsync();

        Assert.Null(result);
        Assert.NotNull(viewModel.LastError);
    }

    [Fact]
    public async Task CreateDiagnosticBundle_WritesTheArchiveAndClearsThePreview()
    {
        var service = new StubDiagnosticBundleService(CreatePreview(blocked: false));
        var viewModel = new HardeningDiagnosticsViewModel(diagnosticBundleService: service);

        await viewModel.PreviewDiagnosticBundleAsync();

        var result = await viewModel.CreateDiagnosticBundleAsync();

        Assert.NotNull(result);
        Assert.True(service.WasCreated);
        Assert.Equal(result!.BundlePath, viewModel.LastBundlePath);
        Assert.False(viewModel.HasExportablePreview);
    }

    [Fact]
    public async Task DatabaseActions_PublishBackupAndIntegrityResults()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            databaseBackupService: new StubDatabaseBackupService());

        var backup = await viewModel.CreateDatabaseBackupAsync();

        Assert.NotNull(backup);
        Assert.Equal(backup!.BackupPath, viewModel.LastBackupPath);

        var integrity = await viewModel.VerifyDatabaseIntegrityAsync();

        Assert.NotNull(integrity);
        Assert.Contains("Integrity ok", viewModel.LastIntegritySummary, StringComparison.Ordinal);
        Assert.Contains("matches sidecar", viewModel.LastIntegritySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashRecovery_PublishesTheRecoverySummary()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            crashRecoveryService: new StubCrashRecoveryService());

        var report = await viewModel.RecoverInterruptedWorkAsync();

        Assert.NotNull(report);
        Assert.Contains("executions=1", viewModel.LastRecoverySummary, StringComparison.Ordinal);
        Assert.Contains("retainedLocks=1", viewModel.LastRecoverySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionCleanup_PublishesTheCleanupSummary()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            retentionCleanupService: new StubRetentionCleanupService());

        var report = await viewModel.RunRetentionCleanupAsync();

        Assert.NotNull(report);
        Assert.Equal(2, report!.DeletedDiagnosticBundleCount);
        Assert.Contains("bundles=2", viewModel.LastRetentionSummary, StringComparison.Ordinal);
        Assert.Contains("preservedActive=4", viewModel.LastRetentionSummary, StringComparison.Ordinal);
        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public async Task QuotaSoak_PublishesTheSoakSummary()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            quotaPollingSoakRunner: new StubQuotaSoakRunner());

        var report = await viewModel.RunQuotaSoakAsync();

        Assert.NotNull(report);
        Assert.True(report!.IsHealthy);
        Assert.Contains("cycles=60/60", viewModel.LastQuotaSoakSummary, StringComparison.Ordinal);
        Assert.Contains("healthy=True", viewModel.LastQuotaSoakSummary, StringComparison.Ordinal);
        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public async Task AcceptanceScenario_PublishesTheScenarioSummaryWithProvenRefusalsAndNoNativeSwitchClaim()
    {
        var viewModel = new HardeningDiagnosticsViewModel(
            endToEndScenarioRunner: new StubEndToEndScenarioRunner());

        var report = await viewModel.RunAcceptanceScenarioAsync();

        Assert.NotNull(report);
        Assert.True(report!.IsSuccessful);
        Assert.Contains("passed=True", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.Contains("runRecovered=True", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.Null(viewModel.LastError);

        // The shipped status line reports the two account-context results as proven refusals and states
        // plainly that no native switch was verified, so a green screen cannot imply a native switch.
        Assert.Contains("agySwitchRefused=True", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.Contains("codexSwitchRefused=True", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.Contains("refusalNamed=True", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.Contains("nativeSwitchVerified=False", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);

        // The former success indicators are gone from the shipped surface entirely.
        Assert.DoesNotContain("agySession", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("codexSession", viewModel.LastAcceptanceSummary, StringComparison.Ordinal);
    }

    private static DiagnosticBundlePreview CreatePreview(bool blocked)
    {
        var finding = new WorkflowSecretFinding("logs/app.log", 1, "OpenAiKey", "sk-live-material");

        return new DiagnosticBundlePreview
        {
            PreviewId = "preview-1",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(15),
            Request = DiagnosticBundleRequest.Default,
            Files = new[]
            {
                new DiagnosticFileItem("logs/app.log", DiagnosticBundleCategories.Logs, 64, new string('a', 64), true)
            },
            SecretScan = blocked
                ? new WorkflowSecretScanReport(true, new[] { finding }, new[] { "logs/app.log" })
                : WorkflowSecretScanReport.Empty,
            BlockingFindings = blocked ? new[] { finding } : Array.Empty<WorkflowSecretFinding>(),
            SecretReferenceCount = 0,
            Warnings = Array.Empty<string>()
        };
    }

    private sealed class StubDiagnosticBundleService : IDiagnosticBundleService
    {
        private readonly DiagnosticBundlePreview _preview;

        public StubDiagnosticBundleService(DiagnosticBundlePreview preview)
        {
            _preview = preview;
        }

        public bool WasCreated { get; private set; }
        public int PreviewCount { get; private set; }
        public Func<Task<DiagnosticBundlePreview>>? PreviewHandler { get; init; }
        public Func<CancellationToken, Task<DiagnosticBundlePreview>>? CancellablePreviewHandler { get; init; }

        public Task<DiagnosticBundlePreview> PreviewAsync(
            DiagnosticBundleRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            PreviewCount++;
            return CancellablePreviewHandler?.Invoke(cancellationToken) ?? PreviewHandler?.Invoke() ?? Task.FromResult(_preview);
        }

        public Task<DiagnosticBundleResult> CreateBundleAsync(
            DiagnosticBundlePreview preview,
            CancellationToken cancellationToken = default)
        {
            WasCreated = true;

            return Task.FromResult(new DiagnosticBundleResult(
                "C:\\exports\\bundle.zip",
                preview.PreviewId,
                preview.Files.Count,
                1024,
                new string('b', 64),
                DateTimeOffset.UnixEpoch,
                "{}"));
        }
    }

    private sealed class StubDatabaseBackupService : IDatabaseBackupService
    {
        public string DatabasePath => "C:\\data\\llmworkgui.db";

        public Task<DatabaseBackupResult> CreateBackupAsync(
            string? destinationPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseBackupResult(
                DatabasePath,
                "C:\\data\\backups\\snapshot.db",
                "C:\\data\\backups\\snapshot.db.sha256",
                2048,
                new string('c', 64),
                IntegrityOk: true,
                SchemaVersion: 1,
                DateTimeOffset.UnixEpoch,
                new[] { "ok" }));
        }

        public Task<DatabaseIntegrityReport> VerifyIntegrityAsync(
            string? databasePath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseIntegrityReport(
                DatabasePath,
                IsHealthy: true,
                new string('c', 64),
                new string('c', 64),
                ChecksumMatches: true,
                SchemaVersion: 1,
                2048,
                DateTimeOffset.UnixEpoch,
                new[] { "ok" }));
        }

        public Task<DatabaseRestoreResult> RestoreAsync(
            string backupPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubCrashRecoveryService : IAppCrashRecoveryService
    {
        public Task<AppCrashRecoveryReport> RecoverWorkflowRunsAfterRestartAsync(
            IReadOnlyList<LLMWorkGUI.Application.Reconciliation.ReconciliationEvidence> reconciliationEvidence,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AppCrashRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppCrashRecoveryReport
            {
                RunAtUtc = DateTimeOffset.UnixEpoch,
                ApplicationInstanceId = "test-instance",
                SkippedAsViewOnly = false,
                InterruptedExecutionIds = new[] { "execution-1" },
                InterruptedSessionIds = Array.Empty<string>(),
                InterruptedWorkflowRunIds = Array.Empty<string>(),
                ReleasedLockIds = Array.Empty<string>(),
                RetainedLockIds = new[] { "lock-1" },
                RehydratedHealthScopeCount = 2,
                Warnings = Array.Empty<string>()
            });
        }
    }

    private sealed class StubRetentionCleanupService : IRetentionCleanupService
    {
        public RetentionPolicy Policy => RetentionPolicy.Default;

        public Task<RetentionCleanupReport> CleanupAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RetentionCleanupReport
            {
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                CompletedAtUtc = DateTimeOffset.UnixEpoch,
                DeletedDiagnosticBundleCount = 2,
                DeletedScratchWorkspaceCount = 1,
                ArchivedAuditRecordCount = 3,
                DeletedAuditRecordCount = 3,
                PreservedActiveItemCount = 4,
                SkippedItemCount = 0
            });
        }
    }

    private sealed class StubQuotaSoakRunner : IQuotaPollingSoakRunner
    {
        public Task<QuotaSoakReport> RunAsync(
            QuotaSoakOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new QuotaSoakReport
            {
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                CompletedAtUtc = DateTimeOffset.UnixEpoch,
                RequestedCycles = 60,
                CompletedCycles = 60,
                SuccessCount = 55,
                FailureCount = 5,
                MaxConsecutiveFailures = 1,
                MinObservedBackoff = TimeSpan.FromSeconds(30),
                MaxObservedBackoff = TimeSpan.FromMinutes(2),
                MaxCycleDuration = TimeSpan.FromMilliseconds(5),
                ManagedMemoryStartBytes = 1024,
                ManagedMemoryEndBytes = 2048,
                MemoryGrowthBudgetBytes = 64L * 1024 * 1024,
                ActiveRefreshCountAtEnd = 0,
                OutstandingTimersAtEnd = 0,
                TimerProbeAttached = true,
                BackgroundSchedulerExercised = true
            });
        }
    }

    private sealed class StubEndToEndScenarioRunner : IEndToEndWorkflowScenarioRunner
    {
        public Task<EndToEndWorkflowScenarioReport> RunAsync(CancellationToken cancellationToken = default)
        {
            var blockers = new[]
            {
                new EndToEndScenarioBlockerEvidence(
                    EndToEndScenarioBlockerIds.MissingReviewer,
                    "Обязательный ревьюер отсутствует",
                    "stage-code-and-ui",
                    true,
                    "detail"),
                new EndToEndScenarioBlockerEvidence(
                    EndToEndScenarioBlockerIds.ConflictingVerdicts,
                    "Конфликт вердиктов",
                    "stage-code-and-ui",
                    true,
                    "detail"),
                new EndToEndScenarioBlockerEvidence(
                    EndToEndScenarioBlockerIds.HashMismatch,
                    "Хэш изменён",
                    "stage-code-and-ui",
                    true,
                    "detail"),
                new EndToEndScenarioBlockerEvidence(
                    EndToEndScenarioBlockerIds.MissingUiArtifact,
                    "UI-артефакт отсутствует",
                    "stage-code-and-ui",
                    true,
                    "detail")
            };

            return Task.FromResult(new EndToEndWorkflowScenarioReport
            {
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                CompletedAtUtc = DateTimeOffset.UnixEpoch,
                BlockerEvidences = blockers,
                ReworkApprovalAllowed = true,
                AgyProfileSwitchRefused = true,
                CodexHomeSwitchRefused = true,
                NativeSwitchProofRefusalNamed = true,
                MirasimHostUntouched = true,
                RunFailureRecovered = true
            });
        }
    }
}
