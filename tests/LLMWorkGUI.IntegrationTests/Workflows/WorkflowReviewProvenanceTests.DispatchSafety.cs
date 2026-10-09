using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData(WorkflowReviewVerdict.Approve)]
    [InlineData(WorkflowReviewVerdict.Reject)]
    [InlineData(WorkflowReviewVerdict.RequestChanges)]
    public async Task DispatchMetadataAloneCannotAuthorizeCallerSuppliedVerdict(WorkflowReviewVerdict value)
    {
        using var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidence = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidence, ExecutionState.Succeeded, RealRouteId);
        var artifact = await CurrentArtifactAsync();
        var verdict = new ReviewerVerdictRecord(ReviewerRole, RealRouteId, artifact.HashSha256, value,
            "This text and verdict were supplied by a caller, not parsed from a stored model response.",
            AssignedAt.AddYears(10), executionId, StageId, artifact.ArtifactId);

        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            provider.GetRequiredService<IWorkflowRunService>().RecordReviewerVerdictAsync(RunId, verdict));

        Assert.Contains("response", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await CurrentRunAsync()).Verdicts);
    }

    [Theory]
    [InlineData("Executions")]
    [InlineData("WorkflowReviewExecutions")]
    public async Task RejectedDispatchInsertLeavesNoOrphanReviewerRows(string rejectedTable)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(
            new WorkflowChannelCatalog(new[] { channel })));
        await StartRunAsync(provider, RealRouteId);
        var before = await ReviewerRowCountsAsync();
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER RejectReviewerDispatch BEFORE INSERT ON {rejectedTable}
                BEGIN SELECT RAISE(ABORT, 'injected reviewer dispatch insert failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(RunId));

        Assert.Equal(before, await ReviewerRowCountsAsync());
        Assert.Equal(0, channel.Calls);
    }

    [Fact]
    public async Task HistoricalCallerVerdictWithoutParsedResponseCannotAdvancePinnedReviewStage()
    {
        using var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidence = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidence, ExecutionState.Succeeded, RealRouteId);
        var artifact = await CurrentArtifactAsync();
        // Reproduce an already-stored pre-fix claim. This bypass is fixture setup, not an API that
        // creates accepted model evidence or a claim that a model really approved this document.
        var historical = await CurrentRunAsync();
        historical.RecordReviewerVerdict(new ReviewerVerdictRecord(ReviewerRole, RealRouteId,
            artifact.HashSha256, WorkflowReviewVerdict.Approve, "Historical caller-supplied claim.",
            AssignedAt.AddMinutes(5), executionId, StageId, artifact.ArtifactId));
        await provider.GetRequiredService<IWorkflowRunRepository>().SaveAsync(historical);

        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            provider.GetRequiredService<IWorkflowRunService>().AdvanceStageAsync(RunId, "Try historical approval."));

        Assert.Contains("response", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StageId, (await CurrentRunAsync()).CurrentStageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReviewerRequestsPersistOnlyTheWinningDispatch(bool retry)
    {
        var channel = new DispatchSafetyChannel();
        var evidence = new BarrierReviewEvidenceRepository(new SqliteWorkflowReviewEvidenceRepository(_database.Factory));
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog(new[] { channel }));
            services.AddSingleton<IWorkflowReviewEvidenceRepository>(evidence);
        });
        await StartRunAsync(provider, RealRouteId);
        if (retry)
            await PersistExecutionAsync(new SqliteWorkflowReviewEvidenceRepository(_database.Factory), ExecutionState.Failed, null);
        var before = await ReviewerRowCountsAsync();
        var service = provider.GetRequiredService<IWorkflowReviewRequestService>();
        var first = service.RequestAssignedReviewAsync(RunId);
        var second = service.RequestAssignedReviewAsync(RunId);
        var failure = await Record.ExceptionAsync(async () =>
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10)));

        var after = await ReviewerRowCountsAsync();
        Assert.Equal((before.Sessions + 1, before.Executions + 1, before.Bindings + 1), after);
        Assert.Null(failure);
        Assert.Equal(1, channel.Calls);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(result => result.IsRefusal));
    }

    [Fact]
    public async Task CallerCancellationAfterDispatchDoesNotLeaveQueuedExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var channel = new DispatchSafetyChannel { OnDispatch = cancellation.Cancel };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(
            new WorkflowChannelCatalog(new[] { channel })));
        await StartRunAsync(provider, RealRouteId);
        var failure = await Record.ExceptionAsync(() => provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(RunId, cancellation.Token));
        var binding = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>()
            .ListByRunIdAsync(RunId));

        Assert.Equal(ExecutionState.Succeeded, binding.ExecutionState);
        var session = await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(binding.SessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Null(session.ActiveExecutionId);
        Assert.Null(failure);
        Assert.Equal(1, channel.Calls);
        Assert.Empty((await CurrentRunAsync()).Verdicts);
    }

    [Fact]
    public async Task ValidationExceptionInsideDispatch_DoesNotProveNonDeliveryOrPermitRetry()
    {
        var channel = new DispatchSafetyChannel { OnDispatch = () => throw new WorkflowValidationException("synthetic post-delivery validation fault") };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        var service = provider.GetRequiredService<IWorkflowReviewRequestService>();
        await service.RequestAssignedReviewAsync(RunId);
        var binding = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        Assert.Equal(ExecutionState.Ambiguous, binding.ExecutionState);
        Assert.True((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Equal(1, channel.Calls);
        var session = await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(binding.SessionId);
        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal(binding.ExecutionId, session.ActiveExecutionId);
    }

    [Theory]
    [InlineData("Sessions")]
    [InlineData("Executions")]
    [InlineData("WorkflowReviewExecutions")]
    public async Task CompletionStorageFaultRollsBackAllReviewerOutcomeRows(string table)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(
            new WorkflowChannelCatalog(new[] { channel })));
        await StartRunAsync(provider, RealRouteId);
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE TRIGGER RejectReviewCompletion BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT, 'injected completion fault'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(RunId));
        var binding = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        Assert.Equal(ExecutionState.Queued, binding.ExecutionState);
        Assert.Null(binding.ObservedRouteId);
        var session = await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(binding.SessionId);
        Assert.Equal(SessionState.Starting, session!.State);
        Assert.Equal(binding.ExecutionId, session.ActiveExecutionId);
        Assert.Equal(1, channel.Calls);
    }

    private async Task<(long Sessions, long Executions, long Bindings)> ReviewerRowCountsAsync()
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Sessions), (SELECT COUNT(*) FROM Executions),
                   (SELECT COUNT(*) FROM WorkflowReviewExecutions);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    // Deterministic fixture metadata only; this channel supplies no model response or native proof.
    private sealed class DispatchSafetyChannel : IWorkflowNodeChannel
    {
        private int _calls;
        public int Calls => _calls;
        public Action? OnDispatch { get; init; }
        public ExecutionState Outcome { get; set; } = ExecutionState.Succeeded;
        public WorkflowModelResponse? Response { get; set; }
        public string? NativeSessionId { get; init; }
        public string ChannelId => "dispatch-safety-fixture";
        public IReadOnlyList<string> Capabilities => new[] { WorkflowReviewRequestService.ReviewerCapability };
        public bool SupportsRoute(string routeId) => routeId == RealRouteId;
        public Task<WorkflowChannelTurnResult> ExecuteTurnAsync(WorkflowChannelTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            OnDispatch?.Invoke();
            return Task.FromResult(new WorkflowChannelTurnResult(Outcome, RealRouteId, NativeSessionId, response: Response));
        }
    }

    private sealed class BarrierReviewEvidenceRepository(IWorkflowReviewEvidenceRepository inner)
        : IWorkflowReviewEvidenceRepository
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public async Task<IReadOnlyList<ReviewerExecutionEvidence>> ListByRunIdAsync(string workflowRunId,
            CancellationToken cancellationToken = default)
        {
            var rows = await inner.ListByRunIdAsync(workflowRunId, cancellationToken);
            var count = Interlocked.Increment(ref _reads);
            if (count <= 2)
            {
                if (count == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return rows;
        }
        public Task<ReviewerExecutionEvidence?> GetByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default) =>
            inner.GetByExecutionIdAsync(executionId, cancellationToken);
        public Task SaveAsync(ReviewerExecutionEvidence evidence, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(evidence, cancellationToken);
        public Task UpdateObservedAsync(ReviewerExecutionEvidence evidence, CancellationToken cancellationToken = default) =>
            inner.UpdateObservedAsync(evidence, cancellationToken);
    }
}
