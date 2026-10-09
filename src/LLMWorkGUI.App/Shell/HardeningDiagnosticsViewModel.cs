using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Shell-side surface for the Phase 12 hardening actions: diagnostic bundle preview/export, database
/// backup and integrity check, startup crash recovery, retention cleanup, the quota polling soak and
/// the end-to-end acceptance scenario. All optional services are injected so the control composes in
/// UI-only graphs and simply reports itself unavailable there.
/// </summary>
public sealed class HardeningDiagnosticsViewModel : ObservableObject
{
    private readonly IDiagnosticBundleService? _diagnosticBundleService;
    private readonly IDatabaseBackupService? _databaseBackupService;
    private readonly IAppCrashRecoveryService? _crashRecoveryService;
    private readonly IRetentionCleanupService? _retentionCleanupService;
    private readonly IQuotaPollingSoakRunner? _quotaPollingSoakRunner;
    private readonly IEndToEndWorkflowScenarioRunner? _endToEndScenarioRunner;

    private readonly CancellationToken _shutdownToken;
    private DiagnosticBundlePreview? _preview;
    private bool _isBusy;
    private string? _statusMessage;
    private string? _lastError;
    private string? _lastBundlePath;
    private string? _lastBackupPath;
    private string? _lastIntegritySummary;
    private string? _lastRecoverySummary;
    private string? _lastRetentionSummary;
    private string? _lastQuotaSoakSummary;
    private string? _lastAcceptanceSummary;

    public HardeningDiagnosticsViewModel(
        IDiagnosticBundleService? diagnosticBundleService = null,
        IDatabaseBackupService? databaseBackupService = null,
        IAppCrashRecoveryService? crashRecoveryService = null,
        IRetentionCleanupService? retentionCleanupService = null,
        IQuotaPollingSoakRunner? quotaPollingSoakRunner = null,
        IEndToEndWorkflowScenarioRunner? endToEndScenarioRunner = null,
        CancellationToken shutdownToken = default)
    {
        _shutdownToken = shutdownToken;
        _diagnosticBundleService = diagnosticBundleService;
        _databaseBackupService = databaseBackupService;
        _crashRecoveryService = crashRecoveryService;
        _retentionCleanupService = retentionCleanupService;
        _quotaPollingSoakRunner = quotaPollingSoakRunner;
        _endToEndScenarioRunner = endToEndScenarioRunner;

        PreviewDiagnosticBundleCommand = new RelayCommand(
            () => _ = PreviewDiagnosticBundleAsync(),
            () => IsDiagnosticBundleAvailable && !IsBusy);

        CreateDiagnosticBundleCommand = new RelayCommand(
            () => _ = CreateDiagnosticBundleAsync(),
            () => HasExportablePreview && !IsBusy);

        CreateDatabaseBackupCommand = new RelayCommand(
            () => _ = CreateDatabaseBackupAsync(),
            () => IsDatabaseBackupAvailable && !IsBusy);

        VerifyDatabaseIntegrityCommand = new RelayCommand(
            () => _ = VerifyDatabaseIntegrityAsync(),
            () => IsDatabaseBackupAvailable && !IsBusy);

        RecoverInterruptedWorkCommand = new RelayCommand(
            () => _ = RecoverInterruptedWorkAsync(),
            () => IsCrashRecoveryAvailable && !IsBusy);

        RunRetentionCleanupCommand = new RelayCommand(
            () => _ = RunRetentionCleanupAsync(),
            () => IsRetentionCleanupAvailable && !IsBusy);

        RunQuotaSoakCommand = new RelayCommand(
            () => _ = RunQuotaSoakAsync(),
            () => IsQuotaSoakAvailable && !IsBusy);

        RunAcceptanceScenarioCommand = new RelayCommand(
            () => _ = RunAcceptanceScenarioAsync(),
            () => IsAcceptanceScenarioAvailable && !IsBusy);
    }

    public ICommand PreviewDiagnosticBundleCommand { get; }

    public ICommand CreateDiagnosticBundleCommand { get; }

    public ICommand CreateDatabaseBackupCommand { get; }

    public ICommand VerifyDatabaseIntegrityCommand { get; }

    public ICommand RecoverInterruptedWorkCommand { get; }

    public ICommand RunRetentionCleanupCommand { get; }

    public ICommand RunQuotaSoakCommand { get; }

    public ICommand RunAcceptanceScenarioCommand { get; }

    public bool IsDiagnosticBundleAvailable => _diagnosticBundleService is not null;

    public bool IsDatabaseBackupAvailable => _databaseBackupService is not null;

    public bool IsCrashRecoveryAvailable => _crashRecoveryService is not null;

    public bool IsRetentionCleanupAvailable => _retentionCleanupService is not null;

    public bool IsQuotaSoakAvailable => _quotaPollingSoakRunner is not null;

    public bool IsAcceptanceScenarioAvailable => _endToEndScenarioRunner is not null;

    public bool IsAvailable =>
        IsDiagnosticBundleAvailable
        || IsDatabaseBackupAvailable
        || IsCrashRecoveryAvailable
        || IsRetentionCleanupAvailable
        || IsQuotaSoakAvailable
        || IsAcceptanceScenarioAvailable;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasExportablePreview => _preview is { IsBlocked: false };

    public bool IsPreviewBlocked => _preview?.IsBlocked ?? false;

    public IReadOnlyList<DiagnosticFileItem> PreviewFiles =>
        _preview?.Files ?? Array.Empty<DiagnosticFileItem>();

    public IReadOnlyList<string> PreviewWarnings =>
        _preview?.Warnings ?? Array.Empty<string>();

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string? LastBundlePath
    {
        get => _lastBundlePath;
        private set => SetProperty(ref _lastBundlePath, value);
    }

    public string? LastBackupPath
    {
        get => _lastBackupPath;
        private set => SetProperty(ref _lastBackupPath, value);
    }

    public string? LastIntegritySummary
    {
        get => _lastIntegritySummary;
        private set => SetProperty(ref _lastIntegritySummary, value);
    }

    public string? LastRecoverySummary
    {
        get => _lastRecoverySummary;
        private set => SetProperty(ref _lastRecoverySummary, value);
    }

    public string? LastRetentionSummary
    {
        get => _lastRetentionSummary;
        private set => SetProperty(ref _lastRetentionSummary, value);
    }

    public string? LastQuotaSoakSummary
    {
        get => _lastQuotaSoakSummary;
        private set => SetProperty(ref _lastQuotaSoakSummary, value);
    }

    public string? LastAcceptanceSummary
    {
        get => _lastAcceptanceSummary;
        private set => SetProperty(ref _lastAcceptanceSummary, value);
    }

    public string UnavailableNote =>
        "Hardening services are not composed in this graph; start the full application graph to use them.";

    public async Task<DiagnosticBundlePreview?> PreviewDiagnosticBundleAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_diagnosticBundleService is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _diagnosticBundleService
                .PreviewAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            _preview = preview;
            RaisePreviewChanged();

            StatusMessage = preview.IsBlocked
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Preview blocked: {0} finding(s) survived redaction.",
                    preview.BlockingFindings.Count)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "Preview ready: {0} file(s), {1} bytes, references={2}.",
                    preview.Files.Count,
                    preview.TotalSizeBytes,
                    preview.SecretReferenceCount);

            return preview;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DiagnosticBundleResult?> CreateDiagnosticBundleAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_diagnosticBundleService is null || _preview is null || _preview.IsBlocked)
        {
            LastError = _diagnosticBundleService is null
                ? UnavailableNote
                : _preview is null
                    ? "A diagnostic bundle preview is required before export."
                    : "The preview is blocked by surviving secret findings and cannot be exported.";
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _diagnosticBundleService
                .CreateBundleAsync(_preview, cancellationToken)
                .ConfigureAwait(true);

            LastBundlePath = result.BundlePath;
            StatusMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Bundle written: {0} ({1} file(s)).",
                result.BundlePath,
                result.FileCount);

            _preview = null;
            RaisePreviewChanged();

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DatabaseBackupResult?> CreateDatabaseBackupAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_databaseBackupService is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _databaseBackupService
                .CreateBackupAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            LastBackupPath = result.BackupPath;
            StatusMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Backup created: {0} ({1} bytes, schema v{2}, integrity {3}).",
                result.BackupPath,
                result.SizeBytes,
                result.SchemaVersion,
                result.IntegrityOk ? "ok" : "failed");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DatabaseIntegrityReport?> VerifyDatabaseIntegrityAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_databaseBackupService is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await _databaseBackupService
                .VerifyIntegrityAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            LastIntegritySummary = string.Format(
                CultureInfo.InvariantCulture,
                "Integrity {0}; schema v{1}; checksum {2}.",
                report.IsHealthy ? "ok" : "failed",
                report.SchemaVersion,
                report.ChecksumMatches switch
                {
                    true => "matches sidecar",
                    false => "does not match sidecar",
                    null => "sidecar absent"
                });

            StatusMessage = LastIntegritySummary;

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<AppCrashRecoveryReport?> RecoverInterruptedWorkAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_crashRecoveryService is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await _crashRecoveryService
                .RecoverAsync(cancellationToken)
                .ConfigureAwait(true);

            LastRecoverySummary = string.Format(
                CultureInfo.InvariantCulture,
                "Recovery: executions={0}, sessions={1}, runs={2}, releasedLocks={3}, "
                + "retainedLocks={4}, healthScopes={5}, viewOnly={6}.",
                report.InterruptedExecutionIds.Count,
                report.InterruptedSessionIds.Count,
                report.InterruptedWorkflowRunIds.Count,
                report.ReleasedLockIds.Count,
                report.RetainedLockIds.Count,
                report.RehydratedHealthScopeCount,
                report.SkippedAsViewOnly);

            StatusMessage = LastRecoverySummary;

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<RetentionCleanupReport?> RunRetentionCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_retentionCleanupService is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await _retentionCleanupService
                .CleanupAsync(cancellationToken)
                .ConfigureAwait(true);

            LastRetentionSummary = string.Format(
                CultureInfo.InvariantCulture,
                "Retention: bundles={0}, scratch={1}, archivedAudit={2}, deletedAudit={3}, "
                + "preservedActive={4}, skipped={5}.",
                report.DeletedDiagnosticBundleCount,
                report.DeletedScratchWorkspaceCount,
                report.ArchivedAuditRecordCount,
                report.DeletedAuditRecordCount,
                report.PreservedActiveItemCount,
                report.SkippedItemCount);

            StatusMessage = LastRetentionSummary;

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<QuotaSoakReport?> RunQuotaSoakAsync(
        QuotaSoakOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_quotaPollingSoakRunner is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await _quotaPollingSoakRunner
                .RunAsync(options, cancellationToken)
                .ConfigureAwait(true);

            LastQuotaSoakSummary = string.Format(
                CultureInfo.InvariantCulture,
                "Quota soak: cycles={0}/{1}, success={2}, failures={3}, healthy={4}, "
                + "activeRefreshes={5}, outstandingTimers={6}, timerProbe={7}.",
                report.CompletedCycles,
                report.RequestedCycles,
                report.SuccessCount,
                report.FailureCount,
                report.IsHealthy,
                report.ActiveRefreshCountAtEnd,
                report.OutstandingTimersAtEnd,
                report.TimerProbeAttached);

            StatusMessage = LastQuotaSoakSummary;

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<EndToEndWorkflowScenarioReport?> RunAcceptanceScenarioAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        if (_endToEndScenarioRunner is null)
        {
            LastError = UnavailableNote;
            return null;
        }

        IsBusy = true;
        LastError = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = operationCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await _endToEndScenarioRunner
                .RunAsync(cancellationToken)
                .ConfigureAwait(true);

            // The two account-context fields report a proven refusal, not a switch: no backend on this
            // host reports the account, the actual model, a unique route key and the native session, so
            // there is no field here a reader could mistake for a verified native switch.
            LastAcceptanceSummary = string.Format(
                CultureInfo.InvariantCulture,
                "Acceptance scenario: passed={0}, missingReviewer={1}, conflictingVerdicts={2}, "
                + "hashMismatch={3}, missingUiArtifact={4}, rework={5}, agySwitchRefused={6}, "
                + "codexSwitchRefused={7}, refusalNamed={8}, nativeSwitchVerified={9}, "
                + "mirasimUntouched={10}, runRecovered={11}.",
                report.IsSuccessful,
                report.MissingReviewerBlocked,
                report.ConflictingVerdictsBlocked,
                report.HashMismatchBlocked,
                report.MissingUiArtifactBlocked,
                report.ReworkApprovalAllowed,
                report.AgyProfileSwitchRefused,
                report.CodexHomeSwitchRefused,
                report.NativeSwitchProofRefusalNamed,
                report.NativeSwitchVerified,
                report.MirasimHostUntouched,
                report.RunFailureRecovered);

            StatusMessage = LastAcceptanceSummary;

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(HasExportablePreview));
        OnPropertyChanged(nameof(IsPreviewBlocked));
        OnPropertyChanged(nameof(PreviewFiles));
        OnPropertyChanged(nameof(PreviewWarnings));
    }
}
