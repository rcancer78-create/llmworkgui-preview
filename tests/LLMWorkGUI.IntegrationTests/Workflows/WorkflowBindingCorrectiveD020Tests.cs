using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowBindingCorrectiveD020Tests : IDisposable
{
    private readonly TestDatabase _database = new();
    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task CorrectiveD020_ConcurrentInitialBindReturnsOnlyTheDurableCompositeIdentity()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        var now = DateTimeOffset.UtcNow;
        var hash = "sha256:" + new string('a', 64);
        var packages = new SqliteWorkflowPackageRepository(_database.Factory);
        var versions = new SqliteWorkflowVersionRepository(_database.Factory);
        await packages.UpsertAsync(new WorkflowPackage("pkg-race", "Race", null, Array.Empty<string>(),
            WorkflowSourceType.ZipArchive, hash, hash, now, now));
        await versions.UpsertAsync(new WorkflowVersion("ver-race", "pkg-race", 1, hash, hash,
            WorkflowSourceType.ZipArchive, null, null, null, null, null, now, null));
        var durable = new SqliteWorkflowBindingRepository(_database.Factory);
        var held = new HeldInitialReads(durable);
        var service = new WorkflowBindingService(held, packages, versions, TimeProvider.System);
        var first = service.BindWorkflowToProjectAsync("project-1", "pkg-race", "ver-race");
        var second = service.BindWorkflowToProjectAsync("project-1", "pkg-race", "ver-race");
        try
        {
            await held.BothRead.WaitAsync(TimeSpan.FromSeconds(10));
            held.Release();
            var results = await Task.WhenAll(first, second);
            var stored = await durable.GetByProjectAndPackageAsync("project-1", "pkg-race");
            Assert.NotNull(stored);
            Assert.Equal(1, await _database.CountAsync("WorkflowBindings"));
            Assert.All(results, result =>
            {
                Assert.Equal(stored.Id, result.Binding.Id);
                Assert.Equal(stored.CreatedAtUtc, result.Binding.CreatedAtUtc);
                Assert.Equal("ver-race", result.Binding.ActiveVersionId);
                Assert.Equal("ver-race", result.ActiveVersion.Id);
            });
            Assert.Single(results, result => result.IsNewBinding);
        }
        finally
        {
            held.Release();
            await Task.WhenAll(first, second);
        }
    }

    private sealed class HeldInitialReads(IWorkflowBindingRepository inner) : IWorkflowBindingRepository
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public Task BothRead => _both.Task;
        public void Release() => _release.TrySetResult();
        public async Task<WorkflowBinding?> GetByProjectAndPackageAsync(string projectId, string workflowPackageId,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken);
            if (Interlocked.Increment(ref _reads) <= 2)
            {
                Assert.Null(result);
                if (Volatile.Read(ref _reads) == 2) _both.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public Task UpsertAsync(WorkflowBinding binding, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(binding, cancellationToken);
        public Task<WorkflowBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);
        public Task<IReadOnlyList<WorkflowBinding>> ListByProjectIdAsync(string projectId,
            CancellationToken cancellationToken = default) => inner.ListByProjectIdAsync(projectId, cancellationToken);
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);
        public Task<WorkflowBinding?> TrySetActiveVersionAsync(WorkflowBinding expected, string activeVersionId,
            DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            inner.TrySetActiveVersionAsync(expected, activeVersionId, updatedAtUtc, cancellationToken);
    }
}
