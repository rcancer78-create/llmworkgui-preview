using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Application.Workflows.Legacy;

/// <summary>
/// Supervised legacy entrypoint runner (ТЗ §6.15, ROADMAP Phase 10B). The execution order is
/// fail-closed: the declared entrypoint is verified first, the checkout writer lock is taken second,
/// the blob is extracted into an isolated <see cref="ScratchScope.Run"/> scratch copy third, and only
/// then is the single opaque process started through <see cref="IProcessSupervisor"/>. Bypass
/// environment variables supplied by the caller are filtered, and the source blob is re-hashed after
/// the operation. The declared script remains authorized local code; this runner supplies no network sandbox.
/// </summary>
public sealed class SupervisedLegacyWorkflowRunner : ISupervisedLegacyWorkflowRunner
{
    public const string UndeclaredEntrypointMessage =
        "Executing a legacy workflow requires an explicitly user-declared entrypoint (ТЗ §6.15). " +
        "Undetected or undeclared scripts cannot be executed.";

    public const string ExecutionIdPrefix = "legacy-";

    public const string LockReleaseReason = "legacy entrypoint execution reached a terminal outcome";

    /// <summary>
    /// Caller-supplied direct credentials and native account contexts omitted from requested child
    /// settings. Process environment inheritance and the declared script remain separate boundaries.
    /// </summary>
    private static readonly HashSet<string> BypassEnvironmentVariableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "OPENAI_API_BASE",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_BASE_URL",
        "GOOGLE_API_KEY",
        "GEMINI_API_KEY",
        "AZURE_OPENAI_API_KEY",
        "CODEX_API_KEY",
        "CODEX_HOME",
        "AGY_API_KEY",
        "PROXY_API_KEY",
        "ADMIN_TOKEN",
        "HOME",
        "USERPROFILE"
    };

    private readonly ICheckoutLockService _checkoutLockService;
    private readonly IScratchWorkspaceManager _scratchWorkspaceManager;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SupervisedLegacyWorkflowRunner> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PendingCleanup, byte> _pendingCleanup = new();
    // Reference identity keeps every resource lease, even if a caller repeats an execution ID.
    private sealed class PendingCleanup(string executionId, ScratchWorkspace workspace, ICheckoutLockToken writer)
    {
        public string ExecutionId { get; } = executionId;
        public ScratchWorkspace Workspace { get; } = workspace;
        public ICheckoutLockToken Writer { get; } = writer;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool NativeCleanupConfirmed;
        public bool ScratchCompleted;
        public bool WriterReleased;
    }

    public SupervisedLegacyWorkflowRunner(
        ICheckoutLockService checkoutLockService,
        IScratchWorkspaceManager scratchWorkspaceManager,
        IProcessSupervisor processSupervisor,
        TimeProvider? timeProvider = null,
        ILogger<SupervisedLegacyWorkflowRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(checkoutLockService);
        ArgumentNullException.ThrowIfNull(scratchWorkspaceManager);
        ArgumentNullException.ThrowIfNull(processSupervisor);

        _checkoutLockService = checkoutLockService;
        _scratchWorkspaceManager = scratchWorkspaceManager;
        _processSupervisor = processSupervisor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SupervisedLegacyWorkflowRunner>.Instance;
    }

    public async Task<LegacyWorkflowExecutionResult> ExecuteAsync(
        LegacyWorkflowExecutionRequest request,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entrypoint = RequireDeclaredEntrypoint(request);
        ValidateBatchArguments(request, entrypoint);
        var executionId = request.ExecutionId ?? ExecutionIdPrefix + Guid.NewGuid().ToString("N");

        ICheckoutLockToken? lockToken = null;
        ScratchWorkspace? workspace = null;
        var cleanupTransferred = false;

        try
        {
            lockToken = await _checkoutLockService
                .AcquireWriterLockAsync(
                    request.ProjectId,
                    request.CanonicalCheckoutPath,
                    executionId,
                    Environment.ProcessId,
                    cancellationToken)
                .ConfigureAwait(false);

            workspace = await _scratchWorkspaceManager
                .CreateWorkspaceAsync(ScratchScope.Run, executionId, cancellationToken)
                .ConfigureAwait(false);

            await _scratchWorkspaceManager
                .ExtractBlobToWorkspaceAsync(request.BlobId, workspace, cancellationToken)
                .ConfigureAwait(false);

            var entrypointPath = ResolveEntrypointPath(workspace.DirectoryPath, entrypoint.Path);

            var specification = BuildSpecification(
                request,
                entrypoint,
                entrypointPath,
                executionId,
                workspace.DirectoryPath);

            using var timeoutCts = request.Timeout is { } timeout
                ? new CancellationTokenSource(timeout, _timeProvider)
                : null;

            using var executionCts = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var processResult = await _processSupervisor
                .ExecuteAsync(specification, outputProgress, executionCts.Token)
                .ConfigureAwait(false);

            var timedOut = timeoutCts?.IsCancellationRequested == true;
            var cancelled = cancellationToken.IsCancellationRequested;

            if (processResult.TerminationReason is ProcessTerminationReason.StartupPending or ProcessTerminationReason.CleanupPending)
            {
                cleanupTransferred = true;
                var retained = new PendingCleanup(executionId, workspace, lockToken);
                _pendingCleanup[retained] = 0;
                _ = CleanupAfterLateStartupAsync(executionId, retained);
                return new LegacyWorkflowExecutionResult
                {
                    ExecutionId = executionId, WorkflowRunId = request.WorkflowRunId,
                    ProcessId = processResult.ProcessId, ProcessState = processResult.TerminationReason == ProcessTerminationReason.StartupPending
                        ? LegacyProcessStates.StartupPending : LegacyProcessStates.CleanupPending,
                    TerminationReason = processResult.TerminationReason, ExitCode = null,
                    StartedAtUtc = processResult.StartedAtUtc, ExitedAtUtc = processResult.ExitedAtUtc,
                    StandardOutputLogPath = processResult.StandardOutputLogPath,
                    StandardErrorLogPath = processResult.StandardErrorLogPath,
                    StandardOutputTail = processResult.StandardOutputTail, StandardErrorTail = processResult.StandardErrorTail,
                    DiscoveredArtifactPaths = Array.Empty<string>(), SourceBlobHashMatches = false
                };
            }

            var sourceBlobHashMatches = await CheckSourceBlobHashAsync(request.BlobId).ConfigureAwait(false);
            var artifactPaths = CollectArtifactPaths(workspace.DirectoryPath);

            _logger.LogInformation(
                "Legacy entrypoint execution {ExecutionId} finished with state {ProcessState} and exit code {ExitCode}; " +
                "source blob hash matched: {SourceBlobHashMatches}.",
                executionId,
                ResolveProcessState(processResult, timedOut, cancelled),
                processResult.ExitCode,
                sourceBlobHashMatches);

            return new LegacyWorkflowExecutionResult
            {
                ExecutionId = executionId,
                WorkflowRunId = request.WorkflowRunId,
                ProcessId = processResult.ProcessId,
                ProcessState = ResolveProcessState(processResult, timedOut, cancelled),
                TerminationReason = processResult.TerminationReason,
                ExitCode = processResult.ExitCode,
                StartedAtUtc = processResult.StartedAtUtc,
                ExitedAtUtc = processResult.ExitedAtUtc,
                StandardOutputLogPath = processResult.StandardOutputLogPath,
                StandardErrorLogPath = processResult.StandardErrorLogPath,
                StandardOutputTail = processResult.StandardOutputTail,
                StandardErrorTail = processResult.StandardErrorTail,
                DiscoveredArtifactPaths = artifactPaths,
                SourceBlobHashMatches = sourceBlobHashMatches
            };
        }
        finally
        {
            try
            {
                if (workspace is not null && !cleanupTransferred)
                {
                    await workspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (lockToken is not null && !cleanupTransferred)
                {
                    try
                    {
                        await lockToken
                            .ReleaseAsync(LockReleaseReason, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            exception,
                            "Failed to release the legacy checkout writer lock for execution {ExecutionId}.",
                            executionId);
                    }
                }
            }
        }
    }

    private async Task CleanupAfterLateStartupAsync(string executionId, PendingCleanup retained)
    {
        await retained.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _processSupervisor.WaitForStartupCleanupAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref retained.NativeCleanupConfirmed, true);
        }
        catch (Exception)
        {
            // Retain both resources on uncertain cleanup. Neither caller cancellation nor a failed
            // cleanup service proves that the native process stopped or that its scratch can be deleted.
            _logger.LogWarning("Legacy late-start cleanup remains unconfirmed; scratch and checkout lock are retained.");
            return;
        }
        finally { retained.Gate.Release(); }

        // A transient owned-resource failure does not invalidate native stop proof. Retry those
        // phases with a bound; remaining leases stay available to explicit repair, without replay.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await RetryConfirmedCleanupAsync(retained, CancellationToken.None).ConfigureAwait(false)) return;
            if (attempt < 4)
                await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<int> RetryPendingCleanupsAsync(CancellationToken cancellationToken = default)
    {
        var repaired = 0;
        foreach (var retained in _pendingCleanup.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RetryConfirmedCleanupAsync(retained, cancellationToken).ConfigureAwait(false)) repaired++;
        }
        return repaired;
    }

    private async Task<bool> RetryConfirmedCleanupAsync(PendingCleanup retained, CancellationToken cancellationToken)
    {
        // The first proof read can fail even though the same supervisor later confirms its owned
        // cleanup. Serialize that recheck with all phases; a stalled proof remains retained.
        if (!await retained.Gate.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
            return false;
        try
        {
            if (!_pendingCleanup.ContainsKey(retained)) return false;
            if (!Volatile.Read(ref retained.NativeCleanupConfirmed))
            {
                await _processSupervisor.WaitForStartupCleanupAsync(retained.ExecutionId, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(3), _timeProvider, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref retained.NativeCleanupConfirmed, true);
            }
            if (!retained.ScratchCompleted)
            {
                await retained.Workspace.CleanupWorkspaceAsync(cancellationToken).ConfigureAwait(false);
                retained.ScratchCompleted = true;
            }
            if (!retained.WriterReleased)
            {
                await retained.Writer.ReleaseAsync("legacy process cleanup confirmed", cancellationToken).ConfigureAwait(false);
                retained.WriterReleased = true;
            }
            return _pendingCleanup.TryRemove(retained, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _logger.LogWarning("Legacy cleanup has pending native proof or owned resources; the lease remains retained for repair.");
            return false;
        }
        finally { retained.Gate.Release(); }
    }

    private static void ValidateBatchArguments(LegacyWorkflowExecutionRequest request, WorkflowEntrypointDescriptor entrypoint)
    {
        if (entrypoint.Kind != WorkflowEntrypointKind.Batch) return;
        if (ContainsBatchGrammar(entrypoint.Path) || request.Arguments.Any(ContainsBatchGrammar))
            throw new WorkflowValidationException("Batch entrypoint arguments contain unsupported shell grammar; use literal arguments.");
    }

    private static bool ContainsBatchGrammar(string? value) => value is null
        || value.IndexOfAny(['"', '&', '|', '<', '>', '^', '(', ')', '%', '!', '\r', '\n']) >= 0;

    private static WorkflowEntrypointDescriptor RequireDeclaredEntrypoint(
        LegacyWorkflowExecutionRequest request)
    {
        var entrypoint = request.DeclaredEntrypoint;

        if (entrypoint is null || !entrypoint.IsDeclared || string.IsNullOrWhiteSpace(entrypoint.Path))
        {
            throw new InvalidOperationException(UndeclaredEntrypointMessage);
        }

        return entrypoint;
    }

    private static string ResolveEntrypointPath(string workspaceDirectory, string declaredPath)
    {
        if (declaredPath.Contains(':'))
        {
            throw new WorkflowValidationException(
                "The declared legacy entrypoint path must be relative to the scratch workspace and must not " +
                "contain drive qualifiers or alternate data streams.");
        }

        if (Path.IsPathRooted(declaredPath))
        {
            throw new WorkflowValidationException(
                "The declared legacy entrypoint path must be relative to the scratch workspace.");
        }

        var normalizedRelativePath = declaredPath.Replace('\\', '/');

        foreach (var segment in normalizedRelativePath.Split('/'))
        {
            if (segment == "..")
            {
                throw new WorkflowValidationException(
                    "The declared legacy entrypoint path must not contain parent directory segments.");
            }
        }

        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));

        var resolvedPath = Path.GetFullPath(
            Path.Combine(workspaceRoot, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsInsideDirectory(workspaceRoot, resolvedPath))
        {
            throw new WorkflowValidationException(
                "The declared legacy entrypoint must be located inside the scratch workspace.");
        }

        if (!File.Exists(resolvedPath))
        {
            throw new WorkflowValidationException(
                $"The declared legacy entrypoint '{declaredPath}' was not found in the scratch copy.");
        }

        return resolvedPath;
    }

    private static ProcessStartSpecification BuildSpecification(
        LegacyWorkflowExecutionRequest request,
        WorkflowEntrypointDescriptor entrypoint,
        string entrypointPath,
        string executionId,
        string workspaceDirectory)
    {
        var (fileName, prefixArguments) = ResolveLaunchCommand(entrypoint.Kind, entrypointPath);
        if (entrypoint.Kind == WorkflowEntrypointKind.Batch && ContainsBatchGrammar(entrypointPath))
            throw new WorkflowValidationException("The batch workspace path contains unsupported shell grammar.");
        var arguments = new List<string>(prefixArguments);
        arguments.AddRange(request.Arguments);
        var environment = ProcessRuntimeEnvironment.CreateBaseline(fileName);
        foreach (var variable in BuildChildEnvironment(request.EnvironmentVariables))
            environment[variable.Key] = variable.Value;

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workspaceDirectory,
            InheritEnvironment = false,
            EnvironmentVariables = environment,
            StdinPolicy = ProcessStdinPolicy.Closed
        };
    }

    private static (string FileName, IReadOnlyList<string> PrefixArguments) ResolveLaunchCommand(
        WorkflowEntrypointKind kind,
        string entrypointPath)
    {
        return kind switch
        {
            WorkflowEntrypointKind.PowerShell => (
                "powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entrypointPath }),
            WorkflowEntrypointKind.Batch => (
                "cmd.exe",
                new[] { "/c", entrypointPath }),
            WorkflowEntrypointKind.Python => (
                OperatingSystem.IsWindows() ? "python.exe" : "python",
                new[] { entrypointPath }),
            WorkflowEntrypointKind.Shell => (
                "sh",
                new[] { entrypointPath }),
            _ => (entrypointPath, Array.Empty<string>())
        };
    }

    private static IReadOnlyDictionary<string, string> BuildChildEnvironment(
        IReadOnlyDictionary<string, string> requestedVariables)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in requestedVariables)
        {
            if (BypassEnvironmentVariableNames.Contains(variable.Key))
            {
                continue;
            }

            environment[variable.Key] = variable.Value;
        }

        return environment;
    }

    private async Task<bool> CheckSourceBlobHashAsync(string blobId)
    {
        try
        {
            await _scratchWorkspaceManager
                .PostOperationSourceHashCheckAsync(blobId, CancellationToken.None)
                .ConfigureAwait(false);

            return true;
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(
                exception,
                "The source workflow blob {BlobId} failed its post-operation SHA-256 integrity check.",
                blobId);

            return false;
        }
    }

    private static IReadOnlyList<string> CollectArtifactPaths(string workspaceDirectory)
    {
        if (!Directory.Exists(workspaceDirectory))
        {
            return Array.Empty<string>();
        }

        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));

        try
        {
            return Directory
                .EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string ResolveProcessState(
        ProcessExecutionResult processResult,
        bool timedOut,
        bool cancelled)
    {
        if (timedOut)
        {
            return LegacyProcessStates.TimedOut;
        }

        if (cancelled)
        {
            return LegacyProcessStates.Terminated;
        }

        return processResult.TerminationReason switch
        {
            ProcessTerminationReason.None => LegacyProcessStates.Exited,
            _ => LegacyProcessStates.Crashed
        };
    }

    private static bool IsInsideDirectory(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
