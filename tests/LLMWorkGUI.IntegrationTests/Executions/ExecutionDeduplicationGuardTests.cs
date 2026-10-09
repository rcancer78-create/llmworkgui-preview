using LLMWorkGUI.Application.Executions;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Executions;

public sealed class ExecutionDeduplicationGuardTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ComputePromptHash_IsCanonicalSha256Hex()
    {
        var guard = await CreateGuardAsync();

        var hash = guard.ComputePromptHash("  hello world  ");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, character => Assert.Contains(character, "0123456789ABCDEF"));
        Assert.Equal(guard.ComputePromptHash("hello world"), hash);
        Assert.Equal(guard.ComputePromptHash("hello world\n"), hash);
    }

    [Fact]
    public async Task AdmitAsync_ReturnsAdmissionWithCanonicalPromptHash()
    {
        var guard = await CreateGuardAsync();
        var clientRequestId = Guid.NewGuid();

        var admission = await guard.AdmitAsync(
            new ExecutionRequest("session-1", clientRequestId, "Fix the failing test"));

        Assert.Equal("session-1", admission.LocalSessionId);
        Assert.Equal(clientRequestId.ToString("D"), admission.ClientRequestId);
        Assert.Equal(guard.ComputePromptHash("Fix the failing test"), admission.PromptHash);
        Assert.Null(admission.RetryOfExecutionId);
    }

    [Fact]
    public async Task AdmitAsync_RejectsPersistedClientRequestId()
    {
        var guard = await CreateGuardAsync();
        var clientRequestId = Guid.NewGuid();

        await _database.SeedExecutionAsync(
            "execution-1",
            clientRequestId: clientRequestId.ToString("D"));

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(new ExecutionRequest("session-1", clientRequestId, "prompt")));
    }

    [Fact]
    public async Task AdmitAsync_RejectsAutomaticRepeatOfActiveClientRequestId()
    {
        var guard = await CreateGuardAsync();
        var clientRequestId = Guid.NewGuid();

        await guard.AdmitAsync(new ExecutionRequest("session-1", clientRequestId, "first prompt"));

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(new ExecutionRequest("session-1", clientRequestId, "second prompt")));
    }

    [Fact]
    public async Task AdmitAsync_RejectsStaleSnapshotAfterFirstRequestPersistsAndReleases()
    {
        var repository = new SnapshotControlledRepository(await CreateRepositoryAsync());
        var guard = new ExecutionDeduplicationGuard(repository);
        var requestId = Guid.NewGuid();
        var first = await guard.AdmitAsync(new ExecutionRequest("session-1", requestId, "first prompt"));
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.AfterSnapshot = async token =>
        {
            snapshotCaptured.TrySetResult();
            await resumeRead.Task.WaitAsync(token);
        };

        var second = guard.AdmitAsync(new ExecutionRequest("session-1", requestId, "second prompt"));
        try
        {
            // A correct guard may reject B before reading. Otherwise its snapshot must
            // be held across A's durable commit and release; no scheduling sleeps.
            await Task.WhenAny(second, snapshotCaptured.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await _database.SeedExecutionAsync("execution-1", clientRequestId: requestId.ToString("D"));
            guard.Release(first);
            resumeRead.TrySetResult();

            await Assert.ThrowsAsync<ExecutionDeduplicationException>(() => second);
        }
        finally
        {
            resumeRead.TrySetResult();
            guard.Release(first);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitAsync_RepositoryFailureOrCancellation_DoesNotStrandReservation(bool cancel)
    {
        var repository = new SnapshotControlledRepository(await CreateRepositoryAsync());
        var guard = new ExecutionDeduplicationGuard(repository);
        var request = new ExecutionRequest("session-1", Guid.NewGuid(), "same request after failed admission");
        using var cancellation = new CancellationTokenSource();
        repository.AfterSnapshot = token =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }

            return Task.FromException(new IOException("synthetic repository failure"));
        };

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => guard.AdmitAsync(request, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => guard.AdmitAsync(request, cancellation.Token));
        }

        repository.AfterSnapshot = null;
        var admission = await guard.AdmitAsync(request);
        Assert.Equal(request.ClientRequestId.ToString("D"), admission.ClientRequestId);
        Assert.Equal(guard.ComputePromptHash(request.Prompt), admission.PromptHash);
        guard.Release(admission);
    }

    [Fact]
    public async Task AdmitAsync_RejectedManualRetry_DoesNotStrandReservation()
    {
        var guard = await CreateGuardAsync();
        var request = new ExecutionRequest("session-1", Guid.NewGuid(), "retry validation", IsManualRetry: true);

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(() => guard.AdmitAsync(request));

        var admission = await guard.AdmitAsync(request with { IsManualRetry = false });
        Assert.Equal(request.ClientRequestId.ToString("D"), admission.ClientRequestId);
        guard.Release(admission);
    }

    [Fact]
    public async Task AdmitAsync_RejectsConcurrentDuplicatePromptHashInSession()
    {
        var guard = await CreateGuardAsync();

        var first = await guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "Same prompt"));

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "  Same prompt  ")));

        guard.Release(first);

        var second = await guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "Same prompt"));

        Assert.NotNull(second);
    }

    [Fact]
    public async Task AdmitAsync_AllowsSamePromptAcrossIndependentSessions()
    {
        var guard = await CreateGuardAsync();
        await _database.SeedSessionAsync("session-2");

        var first = await guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "Same prompt"));
        var second = await guard.AdmitAsync(new ExecutionRequest("session-2", Guid.NewGuid(), "Same prompt"));

        Assert.NotEqual(first.PromptHash, string.Empty);
        Assert.Equal(first.PromptHash, second.PromptHash);
    }

    [Fact]
    public async Task Release_PreviousAdmissionCannotRemoveNewOwnerOfSamePromptHash()
    {
        var guard = await CreateGuardAsync();
        var first = await guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "owned prompt"));
        guard.Release(first);
        var second = await guard.AdmitAsync(new ExecutionRequest("session-1", Guid.NewGuid(), "owned prompt"));
        try
        {
            // A late/finally cleanup of A must be idempotent and cannot free B's reservation.
            guard.Release(first);
            await Assert.ThrowsAsync<ExecutionDeduplicationException>(() => guard.AdmitAsync(
                new ExecutionRequest("session-1", Guid.NewGuid(), "  owned prompt  ")));
        }
        finally { guard.Release(second); }
    }

    [Fact]
    public async Task AdmitAsync_ManualRetry_RequiresRetryOfExecutionId()
    {
        var guard = await CreateGuardAsync();

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(
                new ExecutionRequest("session-1", Guid.NewGuid(), "retry", IsManualRetry: true)));
    }

    [Fact]
    public async Task AdmitAsync_ManualRetry_RejectsUnknownExecutionId()
    {
        var guard = await CreateGuardAsync();

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(
                new ExecutionRequest(
                    "session-1",
                    Guid.NewGuid(),
                    "retry",
                    IsManualRetry: true,
                    RetryOfExecutionId: "missing-execution")));
    }

    [Fact]
    public async Task AdmitAsync_ManualRetry_RejectsNonTerminalExecution()
    {
        var guard = await CreateGuardAsync();

        await _database.SeedExecutionAsync(
            "execution-1",
            state: "Running",
            clientRequestId: Guid.NewGuid().ToString("D"));

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(
                new ExecutionRequest(
                    "session-1",
                    Guid.NewGuid(),
                    "retry",
                    IsManualRetry: true,
                    RetryOfExecutionId: "execution-1")));
    }

    [Fact]
    public async Task AdmitAsync_ManualRetry_AcceptsTerminalExecutionWithNewClientRequestId()
    {
        var guard = await CreateGuardAsync();

        await _database.SeedExecutionAsync(
            "execution-1",
            state: "Failed",
            clientRequestId: Guid.NewGuid().ToString("D"));

        var admission = await guard.AdmitAsync(
            new ExecutionRequest(
                "session-1",
                Guid.NewGuid(),
                "retry",
                IsManualRetry: true,
                RetryOfExecutionId: "execution-1"));

        Assert.Equal("execution-1", admission.RetryOfExecutionId);
    }

    [Fact]
    public async Task AdmitAsync_ManualRetry_RejectsReusedClientRequestId()
    {
        var guard = await CreateGuardAsync();
        var previousClientRequestId = Guid.NewGuid();

        await _database.SeedExecutionAsync(
            "execution-1",
            state: "Failed",
            clientRequestId: previousClientRequestId.ToString("D"));

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(
                new ExecutionRequest(
                    "session-1",
                    previousClientRequestId,
                    "retry",
                    IsManualRetry: true,
                    RetryOfExecutionId: "execution-1")));
    }

    [Fact]
    public async Task AdmitAsync_RejectsRetryOfExecutionIdWithoutManualRetry()
    {
        var guard = await CreateGuardAsync();

        await Assert.ThrowsAsync<ExecutionDeduplicationException>(
            () => guard.AdmitAsync(
                new ExecutionRequest(
                    "session-1",
                    Guid.NewGuid(),
                    "prompt",
                    IsManualRetry: false,
                    RetryOfExecutionId: "execution-1")));
    }

    private async Task<ExecutionDeduplicationGuard> CreateGuardAsync()
    {
        return new ExecutionDeduplicationGuard(await CreateRepositoryAsync());
    }

    private async Task<SqliteExecutionRepository> CreateRepositoryAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();

        return new SqliteExecutionRepository(_database.Factory, new SensitiveDataFilter());
    }

    private sealed class SnapshotControlledRepository(IExecutionRepository inner) : IExecutionRepository
    {
        public Func<CancellationToken, Task>? AfterSnapshot { get; set; }

        public async Task<IReadOnlyList<Execution>> ListBySessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.ListBySessionAsync(sessionId, cancellationToken);
            if (AfterSnapshot is { } callback)
            {
                await callback(cancellationToken);
            }

            return snapshot;
        }

        public Task UpsertAsync(Execution execution, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(execution, cancellationToken);

        public Task<Execution?> GetByIdAsync(string executionId, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(executionId, cancellationToken);

        public Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken = default) =>
            inner.AppendEventAsync(executionEvent, cancellationToken);

        public Task<IReadOnlyList<ExecutionEventRecord>> ListEventsAsync(
            string executionId,
            CancellationToken cancellationToken = default) => inner.ListEventsAsync(executionId, cancellationToken);
    }
}
