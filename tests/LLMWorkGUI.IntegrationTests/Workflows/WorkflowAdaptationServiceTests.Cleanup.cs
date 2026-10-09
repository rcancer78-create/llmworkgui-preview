using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Fact]
    public async Task CommittedSaveWithLockedScratchReturnsSameVersionOnCleanupRetry()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var result = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        string committedId;
        using (var locked = new FileStream(Path.Combine(result.CandidateWorkspacePath, "README.md"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var saved = await _service.SaveCandidateVersionAsync(result.SessionId);
            committedId = saved.VersionId;
            Assert.True(saved.CleanupPending);
            Assert.NotNull(await _versionRepository.GetByIdAsync(committedId));
            Assert.Equal(result.SessionId, _service.GetSessionSnapshot(result.SessionId).SessionId);
            await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                _service.SubmitFollowUpTurnAsync(new(result.SessionId, "must not modify committed candidate")));
            var retry = await _service.SaveCandidateVersionAsync(result.SessionId);
            Assert.True(retry.CleanupPending);
            Assert.Equal(committedId, retry.VersionId);
            Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));
        }
        var cleaned = await _service.SaveCandidateVersionAsync(result.SessionId);
        Assert.False(cleaned.CleanupPending);
        Assert.Equal(committedId, cleaned.VersionId);
        Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));
        Assert.False(Directory.Exists(result.CandidateWorkspacePath));
        Assert.Throws<WorkflowValidationException>(() => _service.GetSessionSnapshot(result.SessionId));
    }

    [Fact]
    public async Task FailedDiscardRetainsSessionForCleanupRetry()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var result = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        using (var locked = new FileStream(Path.Combine(result.CandidateWorkspacePath, "README.md"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => _service.DiscardSessionAsync(result.SessionId));
            Assert.Equal(result.SessionId, _service.GetSessionSnapshot(result.SessionId).SessionId);
        }
        await _service.DiscardSessionAsync(result.SessionId);
        Assert.False(Directory.Exists(result.CandidateWorkspacePath));
        Assert.Throws<WorkflowValidationException>(() => _service.GetSessionSnapshot(result.SessionId));
    }

    [Fact]
    public async Task FailedExpiredCleanupRetainsOwnershipWithoutRefusingAvailableCapacity()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var clock = new AdaptationClock();
        var service = ConcurrentService(new AsyncInvoker((_, _) => Task.FromResult(
            new AdaptationModelResponse(CreateResponse(mappings: new[] { CreateMapping() })))), clock);
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        var old = await service.StartAdaptationAsync(request);
        using (var locked = new FileStream(Path.Combine(old.CandidateWorkspacePath, "README.md"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            clock.UtcNow += TimeSpan.FromHours(25);
            var next = await service.StartAdaptationAsync(request);
            Assert.Equal(old.SessionId, service.GetSessionSnapshot(old.SessionId).SessionId);
            Assert.True(Directory.Exists(old.CandidateWorkspacePath));
            await service.DiscardSessionAsync(next.SessionId);
        }
        await service.DiscardSessionAsync(old.SessionId);
        Assert.False(Directory.Exists(old.CandidateWorkspacePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardAfterSuccessfulExpiryIsIdempotentForStaleDialog(bool saved)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var clock = new AdaptationClock();
        var service = ConcurrentService(new AsyncInvoker((_, _) => Task.FromResult(
            new AdaptationModelResponse(CreateResponse(mappings: new[] { CreateMapping() })))), clock);
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        var old = await service.StartAdaptationAsync(request);
        string? committedId = null;
        if (saved)
        {
            using var locked = new FileStream(Path.Combine(old.CandidateWorkspacePath, "README.md"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await service.SaveCandidateVersionAsync(old.SessionId);
            Assert.True(result.CleanupPending);
            committedId = result.VersionId;
        }
        clock.UtcNow += TimeSpan.FromHours(25);
        var next = await service.StartAdaptationAsync(request);
        Assert.Throws<WorkflowValidationException>(() => service.GetSessionSnapshot(old.SessionId));
        Assert.False(Directory.Exists(old.CandidateWorkspacePath));
        await service.DiscardSessionAsync(old.SessionId);
        await service.DiscardSessionAsync(old.SessionId);
        if (saved) Assert.NotNull(await _versionRepository.GetByIdAsync(committedId!));
        await service.DiscardSessionAsync(next.SessionId);
    }
}
