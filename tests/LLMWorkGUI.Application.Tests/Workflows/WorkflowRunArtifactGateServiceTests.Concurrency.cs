using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowRunArtifactGateServiceTests
{
    [Fact]
    public async Task ArtifactCommitRacingCancellationCannotReviveTheRunOrDiscardItsEvidence()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var priorArtifactIds = run.Artifacts.Select(artifact => artifact.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var paused = new PausedArtifactRepository(_repository);
        var service = new WorkflowRunService(paused, WorkflowScheme.CreateStandardDevelopmentScheme(),
            new InMemoryWorkflowTemplateStore(), _blobStore, new FixedUserApprovalIdentity("operator"), _timeProvider);
        using var content = Content("the in-flight artifact");
        var recording = service.RecordStageArtifactAsync(run.Id, StageId, Kind, content, DataClassification.PrivateSource);
        await paused.BeforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The repository reads/completes synchronously. Without command serialization cancellation
        // commits now, then the suspended old snapshot overwrites it. With serialization it awaits
        // the artifact commit and reads its fresh state. No timing-based sleep selects the order.
        var cancellation = service.CancelRunAsync(run.Id, "Operator cancelled.");
        paused.Release.TrySetResult();
        await Task.WhenAll(recording, cancellation).WaitAsync(TimeSpan.FromSeconds(10));

        var stored = (await _repository.GetByIdAsync(run.Id))!;
        Assert.Equal(WorkflowRunState.Cancelled, stored.State);
        Assert.Equal(priorArtifactIds.Count + 1, stored.Artifacts.Count);
        Assert.All(priorArtifactIds, id => Assert.Contains(stored.Artifacts, artifact => artifact.ArtifactId == id));
        var added = Assert.Single(stored.Artifacts.Where(artifact => !priorArtifactIds.Contains(artifact.ArtifactId)));
        Assert.Equal(StageId, added.StageId);
        Assert.Equal(Kind, added.Kind);
    }

    private sealed class PausedArtifactRepository(IWorkflowRunRepository inner) : IWorkflowRunRepository
    {
        public TaskCompletionSource BeforeCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SaveAsync(WorkflowRun run, CancellationToken token = default) => inner.SaveAsync(run, token);
        public async Task SaveArtifactAsync(WorkflowRun run, WorkflowArtifactEvidence evidence, CancellationToken token = default)
        {
            BeforeCommit.TrySetResult();
            await Release.Task.WaitAsync(token);
            await inner.SaveArtifactAsync(run, evidence, token);
        }
        public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken token = default) => inner.GetByIdAsync(id, token);
        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(string id, CancellationToken token = default) => inner.GetByProjectIdAsync(id, token);
        public Task<WorkflowRun?> GetActiveByProjectIdAsync(string id, CancellationToken token = default) => inner.GetActiveByProjectIdAsync(id, token);
    }
}
