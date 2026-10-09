using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Legacy;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class SupervisedLegacyWorkflowRunnerTests
{
    private const string DefaultCheckoutPath = @"C:\checkout\project";

    private static readonly DateTimeOffset StartedAtUtc = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, ProcessTerminationReason.StartupPending)]
    [InlineData(true, ProcessTerminationReason.StartupPending)]
    [InlineData(false, ProcessTerminationReason.CleanupPending)]
    [InlineData(true, ProcessTerminationReason.CleanupPending)]
    public async Task UnprovenCleanupNeverDeletesScratchOrReleasesWriter(bool cleanupThrows, ProcessTerminationReason reason)
    {
        using var context = new RunnerContext();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ProcessSupervisor.CleanupCompletion = cleanup.Task;
        context.ProcessSupervisor.Handler = (specification, _) => Task.FromResult(
            CreateProcessResult(specification, null, reason));
        if (cleanupThrows) cleanup.SetException(new IOException("Cleanup cannot prove termination."));

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint()));
        Assert.Equal(reason.ToString(), result.ProcessState);
        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionState.Ambiguous, result.ToObservableRunProjection().State);
        Assert.Null(result.ToObservableRunProjection().EndedAtUtc);
        Assert.True(context.LockService.HasActiveLock);
        var workspace = Assert.Single(context.ScratchManager.Workspaces);
        Assert.False(workspace.IsCleanedUp);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "run.ps1")));
        Assert.Empty(result.DiscoveredArtifactPaths);
        // Test-owned resources are released explicitly after proving the negative case.
        // A throwing cleanup contract intentionally stays retained by the runner.
        if (!cleanupThrows)
        {
            cleanup.SetResult();
            for (var i = 0; i < 100 && context.LockService.HasActiveLock; i++) await Task.Delay(10);
            Assert.False(context.LockService.HasActiveLock);
        }
    }

    [Fact]
    public async Task RepeatedExecutionIdRetainsBothResourceLeasesUntilCleanupIsProven()
    {
        using var context = new RunnerContext();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ProcessSupervisor.CleanupCompletion = cleanup.Task;
        context.ProcessSupervisor.Handler = (specification, _) => Task.FromResult(
            CreateProcessResult(specification, null, ProcessTerminationReason.StartupPending));
        var request = CreateRequest(DefaultEntrypoint(), executionId: "repeated-id");
        try
        {
            await context.Runner.ExecuteAsync(request);
            await context.Runner.ExecuteAsync(request);
            Assert.Equal(2, context.LockService.Tokens.Count);
            Assert.All(context.LockService.Tokens, token => Assert.True(token.IsHeld));
            Assert.Equal(2, context.ScratchManager.Workspaces.Count);
            Assert.All(context.ScratchManager.Workspaces, workspace => Assert.False(workspace.IsCleanedUp));
        }
        finally
        {
            cleanup.TrySetResult();
            for (var i = 0; i < 100 && context.LockService.HasActiveLock; i++) await Task.Delay(10);
        }
        Assert.False(context.LockService.HasActiveLock);
        Assert.All(context.ScratchManager.Workspaces, workspace => Assert.True(workspace.IsCleanedUp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRepairRechecksNativeProofWithoutReplayingExecution(bool proofRecovered)
    {
        using var context = new RunnerContext();
        var executions = 0;
        context.ProcessSupervisor.CleanupCompletion = Task.FromException(
            new IOException("Transient proof-service read failure; process ownership is uncertain."));
        context.ProcessSupervisor.Handler = (specification, _) =>
        {
            executions++;
            return Task.FromResult(CreateProcessResult(specification, null,
                ProcessTerminationReason.CleanupPending));
        };

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint()));
        var workspace = Assert.Single(context.ScratchManager.Workspaces);
        Assert.Equal(ExecutionState.Ambiguous, result.ToObservableRunProjection().State);
        Assert.Null(result.ToObservableRunProjection().EndedAtUtc);
        Assert.True(context.LockService.HasActiveLock);
        Assert.False(workspace.IsCleanedUp);

        if (proofRecovered) context.ProcessSupervisor.CleanupCompletion = Task.CompletedTask;
        var repaired = await context.Runner.RetryPendingCleanupsAsync();

        Assert.Equal(proofRecovered ? 1 : 0, repaired);
        Assert.Equal(1, executions); // Repair never reruns the declared script.
        Assert.Equal(!proofRecovered, context.LockService.HasActiveLock);
        Assert.Equal(proofRecovered, workspace.IsCleanedUp);
        Assert.Equal(0, await context.Runner.RetryPendingCleanupsAsync());
        // Result is an immutable original observation; repair supplies no fabricated terminal run.
        Assert.Equal(ExecutionState.Ambiguous, result.ToObservableRunProjection().State);
        Assert.Null(result.ToObservableRunProjection().EndedAtUtc);
    }

    public static IEnumerable<object?[]> UndeclaredEntrypoints()
    {
        yield return new object?[] { null };
        yield return new object?[]
        {
            new WorkflowEntrypointDescriptor("run.ps1", WorkflowEntrypointKind.PowerShell, IsDeclared: false)
        };
        yield return new object?[]
        {
            new WorkflowEntrypointDescriptor("   ", WorkflowEntrypointKind.PowerShell, IsDeclared: true)
        };
    }

    [Theory]
    [MemberData(nameof(UndeclaredEntrypoints))]
    public async Task ExecuteAsync_UndeclaredEntrypoint_IsRefusedBeforeLockScratchOrProcessWork(
        WorkflowEntrypointDescriptor? entrypoint)
    {
        using var context = new RunnerContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Runner.ExecuteAsync(CreateRequest(entrypoint)));

        Assert.Equal(SupervisedLegacyWorkflowRunner.UndeclaredEntrypointMessage, exception.Message);
        Assert.Empty(context.LockService.Acquisitions);
        Assert.Empty(context.ScratchManager.Workspaces);
        Assert.Empty(context.ScratchManager.ExtractedBlobIds);
        Assert.Null(context.ProcessSupervisor.LastSpecification);
    }

    [Fact]
    public async Task ExecuteAsync_AcquiresCheckoutWriterLockAndReleasesItAfterTheRun()
    {
        using var context = new RunnerContext();

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint()));

        var acquisition = Assert.Single(context.LockService.Acquisitions);

        Assert.Equal("project-1", acquisition.ProjectId);
        Assert.Equal(DefaultCheckoutPath, acquisition.CanonicalRootPath);
        Assert.Equal(result.ExecutionId, acquisition.ExecutionId);
        Assert.True(acquisition.ProcessGeneration > 0);
        Assert.StartsWith(SupervisedLegacyWorkflowRunner.ExecutionIdPrefix, result.ExecutionId, StringComparison.Ordinal);

        var token = Assert.Single(context.LockService.Tokens);

        Assert.False(token.IsHeld);
        Assert.Equal(SupervisedLegacyWorkflowRunner.LockReleaseReason, token.ReleaseReason);
        Assert.False(context.LockService.HasActiveLock);
    }

    [Fact]
    public async Task ExecuteAsync_ProvidedExecutionId_BecomesTheLockOwnerAndScratchScope()
    {
        using var context = new RunnerContext();

        var result = await context.Runner.ExecuteAsync(
            CreateRequest(DefaultEntrypoint(), executionId: "persisted-execution-1"));

        Assert.Equal("persisted-execution-1", result.ExecutionId);
        Assert.Equal("persisted-execution-1", Assert.Single(context.LockService.Acquisitions).ExecutionId);
        Assert.Equal("persisted-execution-1", Assert.Single(context.ScratchManager.Workspaces).ScopeId);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectLockConflict_PropagatesBeforeExtractionAndScratchCreation()
    {
        using var context = new RunnerContext();
        context.LockService.AcquireFailure = new ProjectLockConflictException(
            "The checkout already has an active writer lock.");

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint())));

        Assert.Empty(context.ScratchManager.Workspaces);
        Assert.Empty(context.ScratchManager.ExtractedBlobIds);
        Assert.Null(context.ProcessSupervisor.LastSpecification);
    }

    [Fact]
    public async Task ExecuteAsync_RunsStrictlyInsideScratchRunCopyWithoutTouchingProjectCheckout()
    {
        using var context = new RunnerContext();
        var sentinelPath = Path.Combine(context.CheckoutPath, "sentinel.txt");

        await File.WriteAllTextAsync(sentinelPath, "project-content");

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint(),
            canonicalCheckoutPath: context.CheckoutPath));

        Assert.Equal(context.CheckoutPath, Assert.Single(context.LockService.Acquisitions).CanonicalRootPath);

        var workspace = Assert.Single(context.ScratchManager.Workspaces);

        Assert.Equal(ScratchScope.Run, workspace.Scope);
        Assert.Equal(result.ExecutionId, workspace.ScopeId);
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));

        var specification = context.ProcessSupervisor.LastSpecification;

        Assert.NotNull(specification);
        Assert.Equal(workspace.DirectoryPath, specification!.WorkingDirectory);
        Assert.DoesNotContain(
            context.CheckoutPath,
            specification.WorkingDirectory,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(workspace.DirectoryPath, "run.ps1"), specification.Arguments);

        Assert.Equal("project-content", await File.ReadAllTextAsync(sentinelPath));
        Assert.Single(Directory.GetFiles(context.CheckoutPath));
    }

    [Fact]
    public async Task ExecuteAsync_PostOperationSourceHashMismatch_FailsTheRun()
    {
        using var context = new RunnerContext();
        context.ScratchManager.ThrowOnPostOperationHashCheck = true;

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint()));

        Assert.False(result.SourceBlobHashMatches);
        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "blob-1" }, context.ScratchManager.PostOperationHashCheckBlobIds);
        Assert.False(Assert.Single(context.LockService.Tokens).IsHeld);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-legacy";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            using var context = new RunnerContext();
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LEGACY_TASK_TOKEN"] = "scoped-value",
                ["OPENAI_API_KEY"] = "sk-direct-upstream"
            };

            await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint(), environmentVariables: environment));

            var specification = context.ProcessSupervisor.LastSpecification;
            Assert.NotNull(specification);
            Assert.False(specification!.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            Assert.Equal("scoped-value", specification.EnvironmentVariables["LEGACY_TASK_TOKEN"]);
            Assert.DoesNotContain("OPENAI_API_KEY", specification.EnvironmentVariables.Keys);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(specification.FileName);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Equal(baseline["SystemRoot"], specification.EnvironmentVariables["SystemRoot"]);
            Assert.Equal(baseline["windir"], specification.EnvironmentVariables["windir"]);
            Assert.Equal(baseline["ComSpec"], specification.EnvironmentVariables["ComSpec"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FiltersAdapterBypassVariablesAndForwardsStarCliProxySettings()
    {
        using var context = new RunnerContext();

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENAI_API_KEY"] = "sk-direct-upstream",
            ["OPENAI_BASE_URL"] = "https://api.openai.com/v1",
            ["CODEX_API_KEY"] = "codex-direct",
            ["PROXY_API_KEY"] = "proxy-admin-secret",
            ["HOME"] = @"C:\other-home",
            ["STAR_CLIPROXY_URL"] = "http://127.0.0.1:8787",
            ["HTTP_PROXY"] = "http://127.0.0.1:8888",
            ["HTTPS_PROXY"] = "http://127.0.0.1:8888",
            ["CODEX_HOME"] = @"C:\codex-account-home",
            ["LEGACY_TASK_TOKEN"] = "scoped-value"
        };

        var result = await context.Runner.ExecuteAsync(
            CreateRequest(DefaultEntrypoint(), environmentVariables: environment));

        var specification = context.ProcessSupervisor.LastSpecification;

        Assert.NotNull(specification);

        var childEnvironment = specification!.EnvironmentVariables;

        Assert.DoesNotContain("OPENAI_API_KEY", childEnvironment.Keys);
        Assert.DoesNotContain("OPENAI_BASE_URL", childEnvironment.Keys);
        Assert.DoesNotContain("CODEX_API_KEY", childEnvironment.Keys);
        Assert.DoesNotContain("PROXY_API_KEY", childEnvironment.Keys);
        Assert.DoesNotContain("HOME", childEnvironment.Keys);
        Assert.Equal("http://127.0.0.1:8787", childEnvironment["STAR_CLIPROXY_URL"]);
        Assert.Equal("http://127.0.0.1:8888", childEnvironment["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", childEnvironment["HTTPS_PROXY"]);
        Assert.DoesNotContain("CODEX_HOME", childEnvironment.Keys);
        Assert.Equal("scoped-value", childEnvironment["LEGACY_TASK_TOKEN"]);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(WorkflowEntrypointKind.PowerShell, "powershell.exe")]
    [InlineData(WorkflowEntrypointKind.Batch, "cmd.exe")]
    [InlineData(WorkflowEntrypointKind.Shell, "sh")]
    [InlineData(WorkflowEntrypointKind.Other, "run.ps1")]
    public async Task ExecuteAsync_ResolvesTheInterpreterFromTheDeclaredEntrypointKind(
        WorkflowEntrypointKind kind,
        string expectedFileName)
    {
        using var context = new RunnerContext();
        var entrypoint = new WorkflowEntrypointDescriptor("run.ps1", kind, IsDeclared: true);

        await context.Runner.ExecuteAsync(CreateRequest(entrypoint));

        var specification = context.ProcessSupervisor.LastSpecification;

        Assert.NotNull(specification);
        Assert.Equal(expectedFileName, Path.GetFileName(specification!.FileName));
        Assert.Equal("--flag", specification.Arguments[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_DeclaredEntrypointOutsideScratch_IsRefusedAndCleanedUp()
    {
        using var context = new RunnerContext();
        var entrypoint = new WorkflowEntrypointDescriptor("../escape.ps1", WorkflowEntrypointKind.PowerShell, true);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => context.Runner.ExecuteAsync(CreateRequest(entrypoint)));

        Assert.Null(context.ProcessSupervisor.LastSpecification);
        Assert.False(Assert.Single(context.LockService.Tokens).IsHeld);
        Assert.True(context.ScratchManager.Workspaces[0].IsCleanedUp);
    }

    [Theory]
    [InlineData(0, ProcessTerminationReason.None, true)]
    [InlineData(3, ProcessTerminationReason.None, false)]
    [InlineData(0, ProcessTerminationReason.BufferOverflow, false)]
    [InlineData(0, ProcessTerminationReason.UserCancelled, false)]
    public async Task ExecuteAsync_SeparatesProcessExitCodeFromLegacyExecutionOutcome(
        int exitCode,
        ProcessTerminationReason terminationReason,
        bool expectedSuccess)
    {
        using var context = new RunnerContext();

        context.ProcessSupervisor.Handler = (specification, _) => Task.FromResult(
            CreateProcessResult(specification, exitCode, terminationReason));

        var result = await context.Runner.ExecuteAsync(CreateRequest(DefaultEntrypoint(), workflowRunId: "run-42"));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(terminationReason, result.TerminationReason);
        Assert.Equal("run-42", result.WorkflowRunId);
        Assert.Equal(expectedSuccess, result.IsSuccess);

        var projection = result.ToObservableRunProjection();

        Assert.Equal(
            expectedSuccess ? ExecutionState.Succeeded : ExecutionState.Failed,
            projection.State);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReportsTimedOutProcessState()
    {
        using var context = new RunnerContext();

        context.ProcessSupervisor.Handler = async (specification, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return CreateProcessResult(
                specification,
                exitCode: null,
                terminationReason: ProcessTerminationReason.UserCancelled);
        };

        var result = await context.Runner.ExecuteAsync(
            CreateRequest(DefaultEntrypoint(), timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(LegacyProcessStates.TimedOut, result.ProcessState);
        Assert.False(result.IsSuccess);
        Assert.False(Assert.Single(context.LockService.Tokens).IsHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CallerCancellationReachesSupervisorAndCleansScratchAndWriterLock(bool supervisorThrows)
    {
        using var context = new RunnerContext();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        context.ProcessSupervisor.Handler = async (specification, token) =>
        {
            dispatches++;
            entered.TrySetResult();
            try
            {
                await neverCompletes.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                if (supervisorThrows) throw;
            }

            return CreateProcessResult(specification, null, ProcessTerminationReason.UserCancelled);
        };

        var execution = context.Runner.ExecuteAsync(
            CreateRequest(DefaultEntrypoint()), cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var workspace = Assert.Single(context.ScratchManager.Workspaces);
            Assert.True(Directory.Exists(workspace.DirectoryPath));
            Assert.True(context.LockService.HasActiveLock);

            cancellation.Cancel();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (supervisorThrows)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => execution.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(result.IsSuccess);
                Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
            }

            Assert.True(workspace.IsCleanedUp);
            Assert.False(Directory.Exists(workspace.DirectoryPath));
            Assert.False(context.LockService.HasActiveLock);
            Assert.Equal(SupervisedLegacyWorkflowRunner.LockReleaseReason,
                Assert.Single(context.LockService.Tokens).ReleaseReason);
            Assert.Equal(1, dispatches);
        }
        finally
        {
            cancellation.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void ToObservableRunProjection_ReportsUnprovenFieldsAsNotReported()
    {
        var execution = CreateLegacyResult(isSuccess: true);

        var projection = execution.ToObservableRunProjection();

        Assert.Equal(WorkflowRole.Unknown, projection.Role);
        Assert.Equal(LegacyWorkflowProjectionExtensions.LegacyRequestedRouteId, projection.RequestedRouteId);
        Assert.Equal(LegacyWorkflowProjectionExtensions.LegacyDisplayLabel, projection.DisplayLabel);
        Assert.Null(projection.ObservedRouteId);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, projection.ObservedRouteIdDisplay);
        Assert.Null(projection.NativeSessionId);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, projection.NativeSessionIdDisplay);
        Assert.Equal(EvidenceSourceKind.NotReported, projection.EvidenceSource);
        Assert.False(projection.IsSynthetic);
        Assert.Equal(ExecutionState.Succeeded, projection.State);
        Assert.Equal("run-42", projection.SessionId);

        var timelineItem = execution.ToWorkflowRunTimelineItem(sequence: 3);

        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, timelineItem.StageDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, timelineItem.RoleDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, timelineItem.ObservedRouteDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, timelineItem.NativeSessionDisplay);
        Assert.Equal(execution.ExecutionId, timelineItem.ExecutionId);
    }

    private static LegacyWorkflowExecutionResult CreateLegacyResult(bool isSuccess) => new()
    {
        ExecutionId = "legacy-execution-1",
        WorkflowRunId = "run-42",
        ProcessId = 1234,
        ProcessState = isSuccess ? LegacyProcessStates.Exited : LegacyProcessStates.Crashed,
        TerminationReason = ProcessTerminationReason.None,
        ExitCode = isSuccess ? 0 : 1,
        StartedAtUtc = StartedAtUtc,
        ExitedAtUtc = StartedAtUtc.AddSeconds(2),
        StandardOutputLogPath = "stdout.log",
        StandardErrorLogPath = "stderr.log",
        StandardOutputTail = "tail",
        StandardErrorTail = string.Empty,
        DiscoveredArtifactPaths = Array.Empty<string>(),
        SourceBlobHashMatches = true
    };

    private static LegacyWorkflowExecutionRequest CreateRequest(
        WorkflowEntrypointDescriptor? entrypoint = null,
        string? workflowRunId = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        string? executionId = null,
        string? canonicalCheckoutPath = null) =>
        new(
            "project-1",
            canonicalCheckoutPath ?? DefaultCheckoutPath,
            "package-1",
            "version-1",
            "blob-1",
            entrypoint,
            workflowRunId,
            arguments: new[] { "--flag" },
            environmentVariables,
            timeout,
            executionId);

    private static WorkflowEntrypointDescriptor DefaultEntrypoint() =>
        new("run.ps1", WorkflowEntrypointKind.PowerShell, IsDeclared: true);

    private static ProcessExecutionResult CreateProcessResult(
        ProcessStartSpecification specification,
        int? exitCode,
        ProcessTerminationReason terminationReason) => new()
    {
        ExecutionId = specification.ExecutionId,
        ProcessId = 4242,
        TerminationReason = terminationReason,
        ExitCode = exitCode,
        StartedAtUtc = StartedAtUtc,
        ExitedAtUtc = StartedAtUtc.AddSeconds(1),
        RunDirectory = Path.Combine(Path.GetTempPath(), "LLMWorkGUI.Tests", "runs", specification.ExecutionId),
        StandardOutputLogPath = "stdout.log",
        StandardErrorLogPath = "stderr.log",
        StandardOutputBytes = 4,
        StandardErrorBytes = 0,
        StandardOutputHead = "head",
        StandardOutputTail = "tail",
        StandardErrorHead = string.Empty,
        StandardErrorTail = string.Empty,
        OutputOverflowed = terminationReason == ProcessTerminationReason.BufferOverflow
    };

    private sealed class RunnerContext : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public RunnerContext()
        {
            CheckoutPath = Path.Combine(_directory.Root, "checkout");
            Directory.CreateDirectory(CheckoutPath);

            LockService = new RecordingCheckoutLockService();
            ScratchManager = new RecordingScratchWorkspaceManager(Path.Combine(_directory.Root, "appdata"));
            ProcessSupervisor = new RecordingProcessSupervisor();

            Runner = new SupervisedLegacyWorkflowRunner(
                LockService,
                ScratchManager,
                ProcessSupervisor);
        }

        public string CheckoutPath { get; }

        public RecordingCheckoutLockService LockService { get; }

        public RecordingScratchWorkspaceManager ScratchManager { get; }

        public RecordingProcessSupervisor ProcessSupervisor { get; }

        public SupervisedLegacyWorkflowRunner Runner { get; }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class RecordingCheckoutLockService : ICheckoutLockService
    {
        public List<LockAcquisition> Acquisitions { get; } = new();

        public List<RecordingCheckoutLockToken> Tokens { get; } = new();

        public Exception? AcquireFailure { get; set; }

        public bool HasActiveLock => Tokens.Exists(token => token.IsHeld);

        public bool RequiresWriterLock(string? executionMode) => true;

        public bool RequiresWriterLock(WorkflowRole role, string? executionMode) => true;

        public Task<ICheckoutLockToken> AcquireWriterLockAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            CancellationToken cancellationToken = default)
        {
            if (AcquireFailure is not null)
            {
                throw AcquireFailure;
            }

            Acquisitions.Add(new LockAcquisition(projectId, canonicalRootPath, executionId, processGeneration));

            var token = new RecordingCheckoutLockToken(projectId, canonicalRootPath, executionId);
            Tokens.Add(token);

            return Task.FromResult<ICheckoutLockToken>(token);
        }

        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            string? executionMode,
            WorkflowRole role = WorkflowRole.Unknown,
            CancellationToken cancellationToken = default) =>
            AcquireWriterLockAsync(projectId, canonicalRootPath, executionId, processGeneration, cancellationToken)!;
    }

    private sealed class RecordingCheckoutLockToken : ICheckoutLockToken
    {
        public RecordingCheckoutLockToken(string projectId, string canonicalRootPath, string executionId)
        {
            ProjectId = projectId;
            CanonicalRootPath = canonicalRootPath;
            ExecutionId = executionId;
        }

        public string LockId { get; } = Guid.NewGuid().ToString("D");

        public string ProjectId { get; }

        public string CanonicalRootPath { get; }

        public string ExecutionId { get; }

        public string ApplicationInstanceId => "application-instance-1";

        public bool IsHeld { get; private set; } = true;

        public string? ReleaseReason { get; private set; }

        public Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
        {
            ReleaseReason = reason;
            IsHeld = false;

            return Task.CompletedTask;
        }

        public void Dispose() => IsHeld = false;

        public ValueTask DisposeAsync()
        {
            IsHeld = false;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingScratchWorkspaceManager : IScratchWorkspaceManager
    {
        private readonly string _scratchRoot;

        public RecordingScratchWorkspaceManager(string scratchRoot)
        {
            _scratchRoot = scratchRoot;
        }

        public List<ScratchWorkspace> Workspaces { get; } = new();

        public List<string> ExtractedBlobIds { get; } = new();

        public List<string> PostOperationHashCheckBlobIds { get; } = new();

        public bool ThrowOnPostOperationHashCheck { get; set; }

        public Task<ScratchWorkspace> CreateWorkspaceAsync(
            ScratchScope scope,
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            var directoryPath = Path.Combine(_scratchRoot, "scratch", scope.ToWireName(), scopeId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var workspace = new ScratchWorkspace(directoryPath, scope, scopeId);
            Workspaces.Add(workspace);

            return Task.FromResult(workspace);
        }

        public Task ExtractBlobToWorkspaceAsync(
            string blobId,
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            ExtractedBlobIds.Add(blobId);

            var entrypointPath = Path.Combine(workspace.DirectoryPath, "run.ps1");

            Directory.CreateDirectory(Path.GetDirectoryName(entrypointPath)!);
            File.WriteAllText(entrypointPath, "Write-Output 'legacy'");

            return Task.CompletedTask;
        }

        public Task PostOperationSourceHashCheckAsync(
            string blobId,
            CancellationToken cancellationToken = default)
        {
            PostOperationHashCheckBlobIds.Add(blobId);

            if (ThrowOnPostOperationHashCheck)
            {
                throw new InvalidDataException("The source blob failed its SHA-256 integrity check.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProcessSupervisor : IProcessSupervisor
    {
        public Task CleanupCompletion { get; set; } = Task.CompletedTask;
        public Task WaitForStartupCleanupAsync(string executionId, CancellationToken cancellationToken = default) => CleanupCompletion.WaitAsync(cancellationToken);
        public ProcessStartSpecification? LastSpecification { get; private set; }

        public ProcessExecutionResult? Result { get; set; }

        public Exception? Failure { get; set; }

        public Func<ProcessStartSpecification, CancellationToken, Task<ProcessExecutionResult>>? Handler { get; set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            LastSpecification = specification;

            if (Failure is not null)
            {
                throw Failure;
            }

            if (Handler is not null)
            {
                return Handler(specification, cancellationToken);
            }

            return Task.FromResult(Result ?? CreateProcessResult(specification, exitCode: 0, ProcessTerminationReason.None));
        }
    }

    private sealed record LockAcquisition(
        string ProjectId,
        string CanonicalRootPath,
        string ExecutionId,
        long ProcessGeneration);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "LLMWorkGUI.Tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }

                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
