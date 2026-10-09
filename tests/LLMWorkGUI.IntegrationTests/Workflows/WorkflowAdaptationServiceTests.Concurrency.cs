using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    private WorkflowAdaptationService ConcurrentService(IAdaptationModelInvoker invoker, TimeProvider? clock = null) =>
        CreateService(_versionRepository,
            new ChatCapabilityFixtureCatalog(new SanitizedCatalogProvider(_providerRepository, _accountRepository, _healthStateRepository)), invoker, clock);

    [Fact]
    public async Task HungFollowUpDoesNotBlockOtherSessionSnapshotStartSaveOrDiscard()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ConcurrentService(new AsyncInvoker(async (request, token) =>
        {
            if (request.Messages.Last().Content == "blocked follow-up")
            {
                entered.TrySetResult();
                await resume.Task.WaitAsync(token);
            }
            return new(CreateResponse(mappings: new[] { CreateMapping() }));
        }));
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        var busy = await service.StartAdaptationAsync(request);
        var idle = await service.StartAdaptationAsync(request);
        var discard = await service.StartAdaptationAsync(request);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var followUp = service.SubmitFollowUpTurnAsync(new(busy.SessionId, "blocked follow-up"), budget.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            Assert.Equal(idle.SessionId, service.GetSessionSnapshot(idle.SessionId).SessionId);
            Assert.Throws<WorkflowValidationException>(() => service.GetSessionSnapshot(busy.SessionId));
            var next = await service.StartAdaptationAsync(request, budget.Token);
            var saved = await service.SaveCandidateVersionAsync(idle.SessionId, budget.Token);
            Assert.Equal(2, saved.VersionNumber);
            await service.DiscardSessionAsync(discard.SessionId, budget.Token);
            await service.DiscardSessionAsync(next.SessionId, budget.Token);
            Assert.False(followUp.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await followUp;
            await service.DiscardSessionAsync(busy.SessionId);
        }
    }

    [Fact]
    public async Task PendingFirstTurnsCountAgainstCapacityAndCancelledAdmissionDoesNotLeakSlot()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var service = ConcurrentService(new AsyncInvoker(async (_, token) =>
        {
            var count = Interlocked.Increment(ref entered);
            if (count == 8) allEntered.TrySetResult();
            if (count == 9) replacementEntered.TrySetResult();
            await resume.Task.WaitAsync(token);
            return new(CreateResponse(mappings: new[] { CreateMapping() }));
        }));
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        using var cancelledStart = new CancellationTokenSource();
        var starts = Enumerable.Range(0, 8)
            .Select(i => service.StartAdaptationAsync(request, i == 0 ? cancelledStart.Token : default)).ToArray();
        Task<AdaptationCandidateResult>? replacement = null;
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<WorkflowValidationException>(() => service.StartAdaptationAsync(request))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(8, entered);
            cancelledStart.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starts[0]);
            replacement = service.StartAdaptationAsync(request);
            await replacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(9, entered);
            await Assert.ThrowsAsync<WorkflowValidationException>(() => service.StartAdaptationAsync(request));
        }
        finally
        {
            resume.TrySetResult();
            foreach (var start in starts)
            {
                try { await service.DiscardSessionAsync((await start).SessionId); }
                catch (OperationCanceledException) when (cancelledStart.IsCancellationRequested) { }
            }
            if (replacement is not null) await service.DiscardSessionAsync((await replacement).SessionId);
        }
        var next = await service.StartAdaptationAsync(request);
        await service.DiscardSessionAsync(next.SessionId);
    }

    [Fact]
    public async Task QueuedDuplicateSaveCannotPersistRemovedSessionAgain()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ConcurrentService(new AsyncInvoker(async (request, _) =>
        {
            if (request.Messages.Count > 1) { entered.TrySetResult(); await resume.Task; }
            return new(CreateResponse(mappings: new[] { CreateMapping() }));
        }));
        var first = await service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        var turn = service.SubmitFollowUpTurnAsync(new(first.SessionId, "follow-up"));
        Task<SaveCandidateVersionResult>? save1 = null, save2 = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            save1 = service.SaveCandidateVersionAsync(first.SessionId);
            save2 = service.SaveCandidateVersionAsync(first.SessionId);
            Assert.False(save1.IsCompleted);
            Assert.False(save2.IsCompleted);
        }
        finally { resume.TrySetResult(); await turn; }
        var outcomes = await Task.WhenAll(new[] { save1!, save2! }.Select(async save =>
        {
            try { await save; return true; }
            catch (WorkflowValidationException) { return false; }
        }));
        Assert.Single(outcomes.Where(success => success));
        Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task ConcurrentSessionSavesAllocateDifferentVersionNumbers()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var service = ConcurrentService(new AsyncInvoker((_, _) => Task.FromResult(
            new AdaptationModelResponse(CreateResponse(mappings: new[] { CreateMapping() })))));
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        var first = await service.StartAdaptationAsync(request);
        var second = await service.StartAdaptationAsync(request);
        var saved = await Task.WhenAll(service.SaveCandidateVersionAsync(first.SessionId), service.SaveCandidateVersionAsync(second.SessionId));
        Assert.Equal(new[] { 2, 3 }, saved.Select(result => result.VersionNumber).Order().ToArray());
        Assert.Equal(3, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task ExpirationSkipsBusySessionAndReclaimsIdleSession()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var clock = new AdaptationClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ConcurrentService(new AsyncInvoker(async (request, _) =>
        {
            if (request.Messages.Last().Content == "blocked follow-up")
            {
                entered.TrySetResult();
                await resume.Task;
            }
            return new(CreateResponse(mappings: new[] { CreateMapping() }));
        }), clock);
        var request = new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced);
        var busy = await service.StartAdaptationAsync(request);
        var expired = await service.StartAdaptationAsync(request);
        var followUp = service.SubmitFollowUpTurnAsync(new(busy.SessionId, "blocked follow-up"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.UtcNow += TimeSpan.FromHours(25);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var next = await service.StartAdaptationAsync(request, budget.Token);
            Assert.False(Directory.Exists(expired.CandidateWorkspacePath));
            Assert.Throws<WorkflowValidationException>(() => service.GetSessionSnapshot(expired.SessionId));
            Assert.False(followUp.IsCompleted);
            await service.DiscardSessionAsync(next.SessionId);
        }
        finally { resume.TrySetResult(); await followUp; }
        Assert.Equal(2, service.GetSessionSnapshot(busy.SessionId).TurnCount);
        await service.DiscardSessionAsync(busy.SessionId);
    }

    private sealed class AsyncInvoker(Func<AdaptationModelRequest, CancellationToken, Task<AdaptationModelResponse>> invoke)
        : IAdaptationModelInvoker
    {
        public Task<AdaptationModelResponse> InvokeModelAsync(AdaptationModelRequest request, CancellationToken cancellationToken = default) =>
            invoke(request, cancellationToken);
    }

    private sealed class AdaptationClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
