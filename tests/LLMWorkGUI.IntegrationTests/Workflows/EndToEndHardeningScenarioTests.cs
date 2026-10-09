using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// Phase 12 Milestone 12B: the end-to-end hardening acceptance scenario proves every fail-closed
/// pre-coder gate blocker separately, proves that the AGY/Codex account-context switch is refused with
/// the named missing proof instead of claiming a native session, keeps the Mirasim host untouched and
/// recovers a failed run to completion on deterministic stubs only.
/// </summary>
public sealed class EndToEndHardeningScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_ProvesEveryBlockerRefusalAndRecovery()
    {
        var time = new FixedTimeProvider(Now);
        var repository = new InMemoryWorkflowRunRepository();
        var runner = CreateRunner(time, repository);

        var report = await runner.RunAsync();

        Assert.True(report.IsSuccessful, report.Summary + " | " + string.Join(" | ", report.Warnings));
        Assert.Empty(report.Warnings);

        // (a) conflicting verdicts
        Assert.True(report.ConflictingVerdictsBlocked);
        // (b) missing required reviewer
        Assert.True(report.MissingReviewerBlocked);
        // (c) document hash changed after review
        Assert.True(report.HashMismatchBlocked);
        // (d) missing UI artifact
        Assert.True(report.MissingUiArtifactBlocked);

        var conflicting = report.FindBlocker(EndToEndScenarioBlockerIds.ConflictingVerdicts);
        var missingReviewer = report.FindBlocker(EndToEndScenarioBlockerIds.MissingReviewer);
        var hashMismatch = report.FindBlocker(EndToEndScenarioBlockerIds.HashMismatch);
        var missingUiArtifact = report.FindBlocker(EndToEndScenarioBlockerIds.MissingUiArtifact);

        Assert.NotNull(conflicting);
        Assert.Contains("conflicting", conflicting!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(missingReviewer);
        Assert.Contains("missing reviewer", missingReviewer!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(hashMismatch);
        Assert.Contains("hash changed after review", hashMismatch!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(missingUiArtifact);
        Assert.Contains("UI artifact is missing", missingUiArtifact!.Detail, StringComparison.OrdinalIgnoreCase);

        // (e) rework with a unanimous approve allows the transition
        Assert.True(report.ReworkApprovalAllowed);

        // (f) the AGY/Codex account-context switch is refused with the named missing proof, Mirasim stays
        // untouched, and nothing anywhere reports a verified native switch.
        Assert.True(report.AgyProfileSwitchRefused);
        Assert.True(report.CodexHomeSwitchRefused);
        Assert.True(report.NativeSwitchProofRefusalNamed);
        Assert.True(report.MirasimHostUntouched);
        Assert.False(report.NativeSwitchVerified);

        // (g) the failed run stays failed while the recovery run completes
        Assert.True(report.RunFailureRecovered);
        Assert.Equal(2, repository.Runs.Count);
        Assert.Contains(
            repository.Runs,
            run => run.State == WorkflowRunState.Failed
                && run.TerminalOutcome == WorkflowTerminalOutcome.Failed);
        Assert.Contains(
            repository.Runs,
            run => run.State == WorkflowRunState.Completed
                && run.TerminalOutcome == WorkflowTerminalOutcome.Completed);
    }

    [Fact]
    public async Task Run_NeverReportsANativeSessionOrAnObservedRouteForAWellFormedSwitchRequest()
    {
        // The negative control for the false proof this scenario used to accept: a caller that echoed the
        // requested route and a locally generated session id used to produce a green "switch created a new
        // session". The production service no longer accepts an observed route at all and has no session id
        // factory, so the probe must observe a named refusal and no session, whatever the caller supplies.
        var time = new FixedTimeProvider(Now);
        var repository = new InMemoryWorkflowRunRepository();
        var studio = CreateStudio(time, repository);
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true);

        var agy = await studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-agy-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: EndToEndWorkflowScenarioRunner.AgyRouteId,
                MirasimState: mirasim));

        var codexHome = Path.Combine(Path.GetTempPath(), "llmworkgui-codex-home-negative-control");

        var codex = await studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account-alpha",
                PreviousNativeSessionId: "native-codex-old",
                CodexHomePath: codexHome,
                RequestedRouteId: EndToEndWorkflowScenarioRunner.CodexRouteId,
                MirasimState: mirasim));

        foreach (var result in new[] { agy, codex })
        {
            Assert.False(result.IsSwitched);
            Assert.True(result.IsBlocked);
            Assert.Equal(
                WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
                result.Refusal);
            Assert.Null(result.NativeSessionId);
            Assert.Null(result.ObservedRouteId);
            Assert.False(result.CarriesPreviousSession);
            Assert.False(result.CredentialsTransferred);
        }

        // The refusal text is product-visible, names both missing halves, and hides the CODEX_HOME path.
        Assert.Contains("star-cliproxy.exe", agy.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("agy-profile executable", agy.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("unique route key", codex.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(codexHome, codex.FailureReason!, StringComparison.Ordinal);

        // No gateway, process, session, execution or run was written by the refused switch.
        Assert.Empty(repository.Runs);
        Assert.Same(mirasim, agy.MirasimState);
        Assert.Same(mirasim, codex.MirasimState);
        Assert.Equal("mirasim-account-1", mirasim.ActiveAccountId);
        Assert.Equal("relay-active", mirasim.RelayState);
        Assert.True(mirasim.RecordingEnabled);
    }

    [Fact]
    public async Task Run_WithoutItsCollaborators_ReportsNotComposedInsteadOfThrowing()
    {
        var runner = new EndToEndWorkflowScenarioRunner();

        var report = await runner.RunAsync();

        Assert.False(report.IsSuccessful);
        Assert.Empty(report.BlockerEvidences);
        Assert.NotEmpty(report.Warnings);
        Assert.False(report.ReworkApprovalAllowed);
        Assert.False(report.RunFailureRecovered);

        // An unrun scenario still never claims a native switch.
        Assert.False(report.AgyProfileSwitchRefused);
        Assert.False(report.CodexHomeSwitchRefused);
        Assert.False(report.NativeSwitchProofRefusalNamed);
        Assert.False(report.NativeSwitchVerified);
    }

    [Fact]
    public void Report_RejectsAnyFieldThatWouldClaimAVerifiedNativeSwitch()
    {
        // The green report of the hardening screen is composed entirely of proven refusals. There is no
        // field a caller can set to "the native switch happened", so the shipped status line cannot imply
        // one even if a future composition tries.
        var properties = typeof(EndToEndWorkflowScenarioReport)
            .GetProperties()
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("AgyProfileSwitchCreatedNewSession", properties);
        Assert.DoesNotContain("CodexHomeSwitchCreatedNewSession", properties);
        Assert.DoesNotContain("NativeSwitchVerified", properties);
        Assert.Contains("AgyProfileSwitchRefused", properties);
        Assert.Contains("CodexHomeSwitchRefused", properties);
        Assert.Contains("NativeSwitchProofRefusalNamed", properties);
    }

    private static EndToEndWorkflowScenarioRunner CreateRunner(
        TimeProvider time,
        InMemoryWorkflowRunRepository repository)
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var studio = CreateStudio(time, repository);

        // The recovery walk drives the standard scheme, whose stages are gated by a stored artifact, so the
        // runner and the run service behind it share one blob store to record the content each stage produces.
        var blobStore = new InMemoryWorkflowArtifactBlobStore();

        var runService = new WorkflowRunService(
            repository,
            scheme,
            new InMemoryWorkflowTemplateStore(),
            blobStore,
            new WindowsLogonUserApprovalIdentity(),
            time);

        return new EndToEndWorkflowScenarioRunner(
            new PreCoderGateValidator(),
            studio,
            runService,
            scheme,
            time,
            blobStore);
    }

    /// <summary>The production studio service, composed exactly as the shipped graph composes it: with no
    /// session id factory and no adapter that could report a native switch.</summary>
    private static WorkflowStudioService CreateStudio(
        TimeProvider time,
        InMemoryWorkflowRunRepository repository) =>
        new(
            graphValidator: new WorkflowGraphValidator(),
            templateStore: new InMemoryWorkflowTemplateStore(),
            timeProvider: time,
            runRepository: repository,
            packageRepository: null);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InMemoryWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

        public IReadOnlyList<WorkflowRun> Runs => _runs.Values.ToArray();

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;

            return Task.CompletedTask;
        }

        public Task SaveArtifactAsync(
            WorkflowRun run,
            WorkflowArtifactEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;

            return Task.CompletedTask;
        }

        public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_runs.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkflowRun>>(
                _runs.Values.Where(run => run.ProjectId == projectId).ToArray());
        }

        public Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _runs.Values.FirstOrDefault(run => run.ProjectId == projectId && !run.IsTerminal));
        }
    }
}
