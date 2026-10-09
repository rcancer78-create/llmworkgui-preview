using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Fact]
    public async Task OversizedModificationBatch_IsRejectedBeforeAnyCandidateWrite()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["README.md"] = "must not overwrite",
                ["a-first.txt"] = "must not appear",
                ["z-oversized.txt"] = new string('я', WorkflowImportLimits.MaxSingleFileBytes / 2 + 1)
            }));
        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));
        Assert.True(result.HasBlockers);
        Assert.Equal("original", ReadCandidateFile(result, "README.md"));
        Assert.False(File.Exists(Path.Combine(result.CandidateWorkspacePath, "a-first.txt")));
        Assert.False(File.Exists(Path.Combine(result.CandidateWorkspacePath, "z-oversized.txt")));
    }

    [Fact]
    public async Task FailedFollowUpCannotSavePriorEvidenceWithResetCandidateBytes()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var first = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));
        var snapshot = _service.GetSessionSnapshot(first.SessionId);
        _modelInvoker.FailureFactory = _ => new IOException("simulated transport failure");

        await Assert.ThrowsAsync<IOException>(() => _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(first.SessionId, "failed turn")));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _service.SaveCandidateVersionAsync(first.SessionId));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(2, snapshot.TurnHistory.Count);

        _modelInvoker.FailureFactory = null;
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        await _service.SubmitFollowUpTurnAsync(new AdaptationFollowUpRequest(first.SessionId, "retry"));
        await _service.SaveCandidateVersionAsync(first.SessionId);
        Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task OversizedSourceTextIsRefusedBeforeModelInvocation()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1",
            CreateArchive(("README.md", new string('a', 256 * 1024 + 1))));
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced));
        Assert.Equal(0, _modelInvoker.InvocationCount);
    }

    [Fact]
    public async Task SaveWaitsForFollowUpToFinishMutatingTheCandidate()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var first = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        _modelInvoker.ResponseFactory = _ =>
        {
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
            return new AdaptationModelResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        };
        var followUp = Task.Run(() => _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(first.SessionId, "latest turn")));
        Task<SaveCandidateVersionResult>? save = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            save = _service.SaveCandidateVersionAsync(first.SessionId);
            Assert.False(save.IsCompleted);
            Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        }
        finally { resume.Set(); }
        await followUp;
        await (save ?? throw new InvalidOperationException("Save was never started."));
        Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));
    }
}
