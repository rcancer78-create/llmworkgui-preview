using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// The run service's two start paths and the scheme a pinned run is advanced against. The pinning rules
/// themselves are covered by <see cref="WorkflowTemplateExecutionPlanResolverTests"/>.
/// </summary>
public sealed partial class WorkflowRunServicePinnedTemplateTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-2";
    private const string TemplateId = "linear-template";

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryWorkflowRunRepository _repository = new();
    private readonly InMemoryWorkflowTemplateStore _templateStore = new();
    private readonly InMemoryWorkflowArtifactBlobStore _blobStore = new();
    private readonly FakeTimeProvider _timeProvider = new(BaseTime);
    private readonly WorkflowRunService _service;

    public WorkflowRunServicePinnedTemplateTests()
    {
        _service = new WorkflowRunService(
            _repository,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            _templateStore,
            _blobStore,
            new FixedUserApprovalIdentity("DOMAIN\\operator"),
            _timeProvider);
    }

    [Fact]
    public async Task StartRunAsync_PinsTheAssignedTemplateVersionItsGraphAndItsDerivedScheme()
    {
        await AssignAsync(1);

        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);

        Assert.Equal(VersionId, run.WorkflowVersionId);
        Assert.Equal(TemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal("node-a", run.CurrentStageId);
        Assert.Equal("Role A", run.CurrentRole);
        Assert.NotNull(run.TemplateGraphSnapshotJson);
        Assert.NotNull(run.TemplateSchemeSnapshotJson);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task StartRunAsync_LeavesNoRunWhenTheProjectHasNoAssignment()
    {
        await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => _service.StartRunAsync(ProjectId, PackageId, VersionId));

        Assert.Equal(0, _repository.SaveCount);
        Assert.Empty(_repository.Runs);
    }

    [Fact]
    public async Task StartLegacyRunAsync_RecordsNoTemplateAndKeepsTheStandardChain()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);

        Assert.False(run.IsTemplateBacked);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);

        // The standard scheme is artifact-gated, so the legacy run has to record its document before it can
        // move on. What this test is about is which scheme the transition follows, not the gate itself.
        run = await _service.RecordStageArtifactAsync(
            run.Id,
            WorkflowScheme.TaskSpecificationStageId,
            "TaskSpecificationDocument",
            new MemoryStream("task specification"u8.ToArray(), writable: false),
            DataClassification.PrivateSource);

        run = await _service.AdvanceStageAsync(run.Id, "legacy advance");

        Assert.Equal(WorkflowScheme.ArchitectureStageId, run.CurrentStageId);
    }

    [Fact]
    public async Task APinnedRunAdvancesOnItsOwnSchemeAndNotOnTheStandardOne()
    {
        await AssignAsync(1);

        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);

        run = await _service.AdvanceStageAsync(run.Id, "first");
        Assert.Equal("node-b", run.CurrentStageId);
        Assert.Equal("Role B", run.CurrentRole);
        Assert.Equal("node-b", (await _repository.GetByIdAsync(run.Id))!.CurrentStageId);

        run = await _service.AdvanceStageAsync(run.Id, "second");
        Assert.Equal("node-c", run.CurrentStageId);
        Assert.Equal("node-c", (await _repository.GetByIdAsync(run.Id))!.CurrentStageId);

        // The terminal stage of the pinned chain refuses an ordinary advance, which is only observable if
        // the service really resolved the pinned scheme instead of the standard one.
        var blocked = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.AdvanceStageAsync(run.Id, "third"));
        Assert.Contains("terminal stage", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APinnedRunAdvancesWithoutAnyReviewerVerdictOrUserApproval()
    {
        await AssignAsync(1);

        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);

        run = await _service.AdvanceStageAsync(run.Id, "no gates on this chain");

        Assert.Empty(run.Verdicts);
        Assert.Empty(run.Approvals);
        Assert.Single(run.Transitions);
        Assert.Empty(run.Transitions[0].ReviewerVerdicts);
        Assert.Null(run.Transitions[0].UserApproval);
        Assert.Single((await _repository.GetByIdAsync(run.Id))!.Transitions);
    }

    [Fact]
    public async Task ExecutorSuccessStillDoesNotBecomeAWorkflowOutcome()
    {
        await AssignAsync(1);

        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);
        run = await _service.AdvanceStageAsync(run.Id, "first");
        run = await _service.AdvanceStageAsync(run.Id, "second");

        Assert.Equal("node-c", run.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
        Assert.Null(run.EndedAtUtc);
        var persisted = await _repository.GetByIdAsync(run.Id);
        Assert.NotNull(persisted);
        Assert.Equal("node-c", persisted.CurrentStageId);
        Assert.Equal(WorkflowTerminalOutcome.None, persisted.TerminalOutcome);
    }

    [Fact]
    public async Task AdvancingARunWhoseSchemeSnapshotIsBrokenIsRefused()
    {
        await AssignAsync(1);

        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);

        // The stored document is replaced directly in the fake to imitate a corrupted row: the run must
        // refuse to fall back to the process-wide scheme.
        _repository.CorruptSchemeSnapshot(run.Id);

        var blocked = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.AdvanceStageAsync(run.Id, "first"));

        Assert.Contains(run.Id, blocked.Message, StringComparison.Ordinal);
        Assert.Empty(_repository.Runs.Single().Transitions);
    }

    private async Task AssignAsync(int version)
    {
        await _templateStore.SaveAsync(CreateLinearTemplate(version));
        await _templateStore.SaveAssignmentAsync(
            new WorkflowTemplateAssignment($"assignment-{version}", ProjectId, TemplateId, version, BaseTime));
    }

    private static WorkflowTemplateDefinition CreateLinearTemplate(int version) =>
        new(
            TemplateId,
            version,
            "Linear template",
            "A gate-free linear template.",
            new WorkflowGraph(
                "node-a",
                new[]
                {
                    new WorkflowNodeDefinition(
                        "node-a",
                        WorkflowNodeKind.Prompt,
                        "Node A",
                        "Role A",
                        successTargetNodeId: "node-b"),
                    new WorkflowNodeDefinition(
                        "node-b",
                        WorkflowNodeKind.Prompt,
                        "Node B",
                        "Role B",
                        successTargetNodeId: "node-c"),
                    new WorkflowNodeDefinition(
                        "node-c",
                        WorkflowNodeKind.TerminalOutcome,
                        "Node C",
                        "Role C")
                }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            BaseTime);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InMemoryWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

        public IReadOnlyList<WorkflowRun> Runs => _runs.Values.ToArray();

        public int SaveCount { get; private set; }

        public void CorruptSchemeSnapshot(string runId)
        {
            var pinned = _runs[runId];

            _runs[runId] = new WorkflowRun(
                pinned.Id,
                pinned.ProjectId,
                pinned.WorkflowPackageId,
                pinned.WorkflowVersionId,
                pinned.SessionId,
                pinned.State,
                pinned.CurrentStageId,
                pinned.CurrentRole,
                pinned.StartedAtUtc,
                pinned.EndedAtUtc,
                pinned.TerminalOutcome,
                pinned.TerminalReason,
                pinned.Transitions,
                pinned.Verdicts,
                pinned.Approvals,
                pinned.TemplateId,
                pinned.TemplateVersion,
                pinned.TemplateGraphSnapshotJson,
                "not json");
        }

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        {
            SaveCount++;
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

        public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_runs.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>(
                _runs.Values.Where(run => run.ProjectId == projectId).ToArray());

        public Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _runs.Values.FirstOrDefault(run => run.ProjectId == projectId && !run.IsTerminal));
    }
}
