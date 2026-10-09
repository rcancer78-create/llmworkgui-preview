using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_UnroutableCatalogAccountIsRefusedBeforeScratchOrInvocation(bool start)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var catalog = new UnroutableReviewCatalog(new SanitizedCatalogProvider(
            _providerRepository, _accountRepository, _healthStateRepository));
        // Reduced composition proves this public service contract, not a native production-policy bypass.
        var service = CreateService(_versionRepository, catalog);
        await Assert.ThrowsAsync<WorkflowValidationException>(async () =>
        {
            if (start) await service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
            else await service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced);
        });
        Assert.Empty(_scratchManager.CreatedScopes);
        Assert.Equal(0, _modelInvoker.InvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_UnfinishedFollowUpDoesNotConsumeAConversationTurnOrContaminateRetry(bool cancelled)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var first = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        var original = _service.GetSessionSnapshot(first.SessionId);
        _modelInvoker.FailureFactory = _ => cancelled
            ? new OperationCanceledException("fixture invocation cancelled before completion")
            : new IOException("fixture transport failed before completion");
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _service.SubmitFollowUpTurnAsync(new(first.SessionId, "unfinished request")));
        else
            await Assert.ThrowsAsync<IOException>(() =>
                _service.SubmitFollowUpTurnAsync(new(first.SessionId, "unfinished request")));
        var failed = _service.GetSessionSnapshot(first.SessionId);
        Assert.Equal(original.TurnHistory, failed.TurnHistory);
        Assert.Equal(original.TurnCount, failed.TurnCount);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _service.SaveCandidateVersionAsync(first.SessionId));

        _modelInvoker.FailureFactory = null;
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        await _service.SubmitFollowUpTurnAsync(new(first.SessionId, "completed retry"));
        var retry = _modelInvoker.Requests.Last();
        Assert.DoesNotContain(retry.Messages, message => message.Content == "unfinished request");
        Assert.Equal("completed retry", retry.Messages.Last().Content);
        Assert.Equal(original.TurnCount + 1, _service.GetSessionSnapshot(first.SessionId).TurnCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Review_FollowUpSystemInstructionUsesTheCurrentExplicitSemanticScope(bool initial, bool current)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var first = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced,
            allowExpandedSemanticScope: initial));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        await _service.SubmitFollowUpTurnAsync(new(first.SessionId, "new scope for this turn", current));
        Assert.Equal(new WorkflowAdaptationPromptBuilder().BuildSystemPrompt(AdaptationGoal.Balanced, current),
            _modelInvoker.Requests.Last().SystemPrompt);
        Assert.Equal(current, _service.GetSessionSnapshot(first.SessionId).AllowExpandedSemanticScope);
    }

    [Fact]
    public async Task Review_IndependentServiceInstancesAllocateDistinctCandidateNumbersAtomically()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var repository = new ReviewCandidateRepository(_versionRepository, synchronizeLists: true);
        var left = CreateService(repository);
        var right = CreateService(repository);
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var first = await left.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var second = await right.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        var leftSave = left.SaveCandidateVersionAsync(first.SessionId);
        var rightSave = right.SaveCandidateVersionAsync(second.SessionId);
        SaveCandidateVersionResult[] saves;
        try
        {
            saves = await Task.WhenAll(leftSave, rightSave).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception error)
        {
            throw new TimeoutException(
                $"left={leftSave.Status}; right={rightSave.Status}; poolThreads={ThreadPool.ThreadCount}; pending={ThreadPool.PendingWorkItemCount}; context={SynchronizationContext.Current?.GetType().FullName ?? "none"}; leftError={leftSave.Exception}; rightError={rightSave.Exception}",
                error);
        }
        Assert.Equal(new[] { 2, 3 }, saves.Select(saved => saved.VersionNumber).Order().ToArray());
        Assert.Equal(2, saves.Select(saved => saved.VersionId).Distinct().Count());
        Assert.Equal(3, (await _versionRepository.ListByPackageIdAsync(package.Id)).Count);
    }

    [Fact]
    public async Task Review_SaveRetryAfterACommittedWriteFailureReusesTheExactCandidateIdentity()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        var repository = new ReviewCandidateRepository(_versionRepository, failAfterFirstWrite: true);
        var service = CreateService(repository);
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var session = await service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        await Assert.ThrowsAsync<IOException>(() => service.SaveCandidateVersionAsync(session.SessionId));
        var committed = Assert.Single((await _versionRepository.ListByPackageIdAsync(package.Id))
            .Where(row => row.Id != version.Id));
        var retry = await service.SaveCandidateVersionAsync(session.SessionId);
        Assert.Equal(committed.Id, retry.VersionId);
        Assert.Equal(committed.VersionNumber, retry.VersionNumber);
        Assert.Equal(committed.CreatedAtUtc, retry.CreatedAtUtc);
        Assert.Equal(2, (await _versionRepository.ListByPackageIdAsync(package.Id)).Count);
        Assert.Equal(1, _modelInvoker.InvocationCount);
    }

    private sealed class UnroutableReviewCatalog(ISanitizedCatalogProvider inner) : ISanitizedCatalogProvider
    {
        public async Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(CancellationToken cancellationToken = default)
        {
            var catalog = await inner.GetSanitizedCatalogAsync(cancellationToken);
            return new(catalog.Providers, catalog.Models.Select(row => row with { IsRoutable = false }).ToArray(),
                catalog.GeneratedAtUtc);
        }
    }

    private sealed class ReviewCandidateRepository(IWorkflowVersionRepository inner,
        bool synchronizeLists = false, bool failAfterFirstWrite = false) : IWorkflowVersionRepository
    {
        private readonly TaskCompletionSource _bothSnapshots = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listCount;
        private int _writeCount;
        public async Task UpsertAsync(WorkflowVersion version, CancellationToken cancellationToken = default)
        {
            await inner.UpsertAsync(version, cancellationToken);
            if (failAfterFirstWrite && Interlocked.Increment(ref _writeCount) == 1)
                throw new IOException("fixture response lost after actual SQLite commit");
        }
        public async Task<WorkflowVersion> InsertCandidateAsync(WorkflowVersion candidate,
            CancellationToken cancellationToken = default)
        {
            if (synchronizeLists)
            {
                if (Interlocked.Increment(ref _listCount) == 2) _bothSnapshots.TrySetResult();
                await _bothSnapshots.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            var committed = await inner.InsertCandidateAsync(candidate, cancellationToken);
            if (failAfterFirstWrite && Interlocked.Increment(ref _writeCount) == 1)
                throw new IOException("fixture response lost after actual SQLite commit");
            return committed;
        }
        public Task<WorkflowVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
            => inner.GetByIdAsync(id, cancellationToken);
        public Task<WorkflowVersion?> GetByPackageAndVersionAsync(string packageId, int versionNumber,
            CancellationToken cancellationToken = default) => inner.GetByPackageAndVersionAsync(packageId, versionNumber, cancellationToken);
        public async Task<IReadOnlyList<WorkflowVersion>> ListByPackageIdAsync(string packageId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.ListByPackageIdAsync(packageId, cancellationToken);
            if (synchronizeLists)
            {
                if (Interlocked.Increment(ref _listCount) == 2) _bothSnapshots.TrySetResult();
                await _bothSnapshots.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return snapshot;
        }
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(id, cancellationToken);
    }
}
