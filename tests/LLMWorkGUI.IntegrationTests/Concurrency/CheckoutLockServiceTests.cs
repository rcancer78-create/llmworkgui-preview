using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed partial class CheckoutLockServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task AcquireWriterLockAsync_BlocksSecondWriterForSameCheckout()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        await using var first = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        Assert.True(first.IsHeld);
        Assert.Equal(ProjectLock.CanonicalizeRoot(rootPath), first.CanonicalRootPath);
        Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(rootPath));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-2", 1));

        await first.DisposeAsync();

        Assert.False(first.IsHeld);
        Assert.Null(await harness.Repository.GetActiveByRootPathAsync(rootPath));

        await using var second = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-2",
            1);

        Assert.True(second.IsHeld);
        Assert.Equal("execution-2", second.ExecutionId);
    }

    [Fact]
    public async Task AcquireLockForExecutionAsync_AllowsParallelReadOnlyExecutions()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        await using var writer = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        var readOnly = await harness.Service.AcquireLockForExecutionAsync(
            "project-1",
            rootPath,
            "execution-2",
            1,
            "plan");

        Assert.Null(readOnly);
        Assert.False(harness.Service.RequiresWriterLock("plan"));
        Assert.False(harness.Service.RequiresWriterLock("ask"));
        Assert.False(harness.Service.RequiresWriterLock("diff"));
        Assert.False(harness.Service.RequiresWriterLock("review"));
        Assert.True(harness.Service.RequiresWriterLock("agent"));
        Assert.True(harness.Service.RequiresWriterLock("edit"));
        Assert.True(harness.Service.RequiresWriterLock("write"));
        Assert.True(harness.Service.RequiresWriterLock(null));
        Assert.True(harness.Service.RequiresWriterLock("unknown-mode"));

        Assert.False(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "plan"));
        Assert.False(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "ask"));
        Assert.False(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "diff"));
        Assert.False(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "review"));
        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "agent"));
        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "write"));
        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, null));
        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Executor, "agent"));
        Assert.True(writer.IsHeld);
    }

    [Fact]
    public async Task AcquireLockForExecutionAsync_ForReviewerWriterMode_BlocksConcurrentWriter()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        await using var writer = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "agent"));
        Assert.True(harness.Service.RequiresWriterLock(WorkflowRole.Reviewer, "write"));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => harness.Service.AcquireLockForExecutionAsync(
                "project-1",
                rootPath,
                "execution-2",
                1,
                "agent",
                WorkflowRole.Reviewer));

        Assert.True(writer.IsHeld);
    }

    [Fact]
    public async Task AcquireLockForExecutionAsync_ForReviewerReadOnlyMode_DoesNotTakeWriterLock()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        await using var writer = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        var readOnly = await harness.Service.AcquireLockForExecutionAsync(
            "project-1",
            rootPath,
            "execution-2",
            1,
            "review",
            WorkflowRole.Reviewer);

        Assert.Null(readOnly);
        Assert.True(writer.IsHeld);
    }

    [Fact]
    public async Task AcquireLockForExecutionAsync_ForWriterMode_BlocksConcurrentWriter()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        await using var writer = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => harness.Service.AcquireLockForExecutionAsync(
                "project-1",
                rootPath,
                "execution-2",
                1,
                "agent"));
    }

    [Fact]
    public async Task AcquireWriterLockAsync_AllowsIndependentCheckouts()
    {
        using var harness = await CreateHarnessAsync();
        var firstRoot = _database.GetWorkspacePath("workspace-a");
        var secondRoot = _database.GetWorkspacePath("workspace-b");

        await using var first = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            firstRoot,
            "execution-1",
            1);

        await using var second = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            secondRoot,
            "execution-2",
            1);

        Assert.True(first.IsHeld);
        Assert.True(second.IsHeld);
        Assert.NotEqual(first.LockId, second.LockId);
        Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(firstRoot));
        Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(secondRoot));
    }

    [Fact]
    public async Task AcquireWriterLockAsync_WhenNamedMutexHeldByAnotherProcess_Throws()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();
        var mutexName = NamedMutexNames.ForCheckout(rootPath);

        using var holderReady = new ManualResetEventSlim(false);
        using var holderRelease = new ManualResetEventSlim(false);

        var holderThread = new Thread(() =>
        {
            var mutex = new Mutex(initiallyOwned: false, mutexName, out _);
            mutex.WaitOne(0);
            holderReady.Set();
            holderRelease.Wait();
            mutex.ReleaseMutex();
            mutex.Dispose();
        })
        {
            IsBackground = true
        };

        holderThread.Start();
        holderReady.Wait();

        try
        {
            await Assert.ThrowsAsync<ProjectLockConflictException>(
                () => harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-1", 1));
        }
        finally
        {
            holderRelease.Set();
            holderThread.Join();
        }

        await using var token = await harness.Service.AcquireWriterLockAsync(
            "project-1",
            rootPath,
            "execution-1",
            1);

        Assert.True(token.IsHeld);
    }

    [Fact]
    public async Task AcquireWriterLockAsync_WhenInstanceIsViewOnly_ThrowsSecondaryInstanceReadOnly()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1");

        var repository = new SqliteProjectLockRepository(_database.Factory);
        var appDataDirectory = Path.Combine(_database.Root, "appdata");

        using var primary = new ApplicationInstanceGuard(appDataDirectory);

        var secondary = RunOnDedicatedThread(() => new ApplicationInstanceGuard(appDataDirectory));

        using (secondary)
        {
            Assert.True(secondary.IsViewOnly);

            var service = new CheckoutLockService(repository, secondary, TimeProvider.System);

            await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(
                () => service.AcquireWriterLockAsync(
                    "project-1",
                    _database.GetWorkspacePath(),
                    "execution-1",
                    1));
        }

        Assert.True(primary.IsPrimarySupervisor);
    }

    [Fact]
    public async Task ReleaseAsync_WhenExecutionIsAmbiguous_IsBlockedUntilReconciliation()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        var token = await harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-1", 1);

        await _database.UpdateExecutionStateAsync("execution-1", "Ambiguous");

        await Assert.ThrowsAsync<ProjectLockConflictException>(() => token.DisposeAsync().AsTask());

        Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(rootPath));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-2", 1));
    }

    [Fact]
    public async Task ReleaseAsync_WhenSessionIsOrphaned_IsBlockedUntilReconciliation()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        var token = await harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-1", 1);

        await _database.UpdateSessionStateAsync("session-1", "Orphaned");

        await Assert.ThrowsAsync<ProjectLockConflictException>(() => token.DisposeAsync().AsTask());

        Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(rootPath));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-2", 1));
    }

    [Fact]
    public async Task ReleaseAsync_AfterReconciliation_RemovesStaleLock()
    {
        using var harness = await CreateHarnessAsync();
        var rootPath = _database.GetWorkspacePath();

        var token = await harness.Service.AcquireWriterLockAsync("project-1", rootPath, "execution-1", 1);

        await _database.UpdateExecutionStateAsync("execution-1", "Ambiguous");
        await Assert.ThrowsAsync<ProjectLockConflictException>(() => token.DisposeAsync().AsTask());

        await _database.UpdateExecutionStateAsync("execution-1", "Succeeded");

        await token.DisposeAsync();

        Assert.Null(await harness.Repository.GetActiveByRootPathAsync(rootPath));
    }

    private async Task<TestHarness> CreateHarnessAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1");
        await _database.SeedExecutionAsync("execution-2");

        var repository = new SqliteProjectLockRepository(_database.Factory);
        var guard = new ApplicationInstanceGuard(Path.Combine(_database.Root, "appdata"));
        var service = new CheckoutLockService(repository, guard, TimeProvider.System);

        return new TestHarness(repository, guard, service);
    }

    private static T RunOnDedicatedThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Dedicated thread execution failed.", failure);
        }

        return result!;
    }

    private sealed class TestHarness : IDisposable
    {
        public TestHarness(
            SqliteProjectLockRepository repository,
            ApplicationInstanceGuard guard,
            CheckoutLockService service)
        {
            Repository = repository;
            Guard = guard;
            Service = service;
        }

        public SqliteProjectLockRepository Repository { get; }

        public ApplicationInstanceGuard Guard { get; }

        public CheckoutLockService Service { get; }

        public void Dispose()
        {
            Guard.Dispose();
        }
    }
}
