using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class ProjectLockRepositoryTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_GuardsSingleWriterPerCanonicalPath()
    {
        var repository = await CreateRepositoryAsync();
        var rootPath = _database.GetWorkspacePath();

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-1", "execution-1", rootPath)));
        Assert.False(await repository.TryAcquireAsync(CreateLock("lock-2", "execution-2", rootPath)));

        var active = await repository.GetActiveByRootPathAsync(rootPath);

        Assert.NotNull(active);
        Assert.Equal("lock-1", active!.Id);
        Assert.True(active.IsHeld);
        Assert.Equal("execution-1", active.ExecutionId);

        Assert.True(await repository.TryAcquireAsync(CreateLock(
            "lock-3",
            "execution-2",
            _database.GetWorkspacePath("workspace-2"))));
    }

    [Fact]
    public async Task TryAcquireAsync_TreatsWindowsCaseVariantsAsSameCanonicalPath()
    {
        var repository = await CreateRepositoryAsync();
        var rootPath = _database.GetWorkspacePath("workspace");
        var caseVariantPath = _database.GetWorkspacePath("WORKSPACE");

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-1", "execution-1", rootPath)));
        Assert.False(await repository.TryAcquireAsync(CreateLock("lock-2", "execution-2", caseVariantPath)));

        Assert.NotNull(await repository.GetActiveByRootPathAsync(caseVariantPath));
    }

    [Fact]
    public async Task ReleaseAsync_AllowsReacquisitionAndRejectsDoubleRelease()
    {
        var repository = await CreateRepositoryAsync();
        var rootPath = _database.GetWorkspacePath();

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-1", "execution-1", rootPath)));
        Assert.True(await repository.ReleaseAsync("lock-1", DateTimeOffset.UtcNow, "completed"));
        Assert.Null(await repository.GetActiveByRootPathAsync(rootPath));
        Assert.False(await repository.ReleaseAsync("lock-1", DateTimeOffset.UtcNow, "completed"));

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-2", "execution-2", rootPath)));
        Assert.Equal("lock-2", (await repository.GetActiveByRootPathAsync(rootPath))!.Id);
    }

    [Fact]
    public async Task ReleaseAsync_RejectsLockWithAmbiguousExecution()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1", state: "Ambiguous");

        var repository = new SqliteProjectLockRepository(_database.Factory);
        var rootPath = _database.GetWorkspacePath();

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-1", "execution-1", rootPath)));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => repository.ReleaseAsync("lock-1", DateTimeOffset.UtcNow, "reconciled"));

        Assert.NotNull(await repository.GetActiveByRootPathAsync(rootPath));
    }

    [Fact]
    public async Task ReleaseAsync_RejectsLockWithOrphanedSession()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync(state: "Orphaned");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var repository = new SqliteProjectLockRepository(_database.Factory);
        var rootPath = _database.GetWorkspacePath();

        Assert.True(await repository.TryAcquireAsync(CreateLock("lock-1", "execution-1", rootPath)));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => repository.ReleaseAsync("lock-1", DateTimeOffset.UtcNow, "reconciled"));

        Assert.NotNull(await repository.GetActiveByRootPathAsync(rootPath));
    }

    private async Task<SqliteProjectLockRepository> CreateRepositoryAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1");
        await _database.SeedExecutionAsync("execution-2");

        return new SqliteProjectLockRepository(_database.Factory);
    }

    private ProjectLock CreateLock(string id, string executionId, string? rootPath = null)
    {
        return ProjectLock.Acquire(
            id,
            "project-1",
            rootPath ?? _database.GetWorkspacePath(),
            executionId,
            "instance-1",
            1,
            DateTimeOffset.UtcNow);
    }
}
