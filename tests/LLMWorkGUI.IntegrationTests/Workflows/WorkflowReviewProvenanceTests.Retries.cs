using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawRetryBinding_RequiresAMatchingKnownPredecessor(bool selfReference)
    {
        using var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var repository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var previousId = await PersistExecutionAsync(repository, ExecutionState.Failed, null);
        var prior = (await repository.GetByExecutionIdAsync(previousId))!;
        var nextId = Guid.NewGuid().ToString("N");
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,RetryOfExecutionId,CreatedAtUtc)
            SELECT $next,SessionId,$next,'Queued','None',RequestedRouteId,$previous,CreatedAtUtc FROM Executions WHERE Id=$old;
            """;
        command.Parameters.AddWithValue("$next", nextId);
        command.Parameters.AddWithValue("$previous", selfReference ? nextId : DBNull.Value);
        command.Parameters.AddWithValue("$old", previousId);
        await command.ExecuteNonQueryAsync();
        var retry = new LLMWorkGUI.Domain.ValueObjects.ReviewerExecutionEvidence(nextId, prior.SessionId, RunId,
            ReviewerRole, StageId, RealRouteId, null, prior.ReviewedArtifactId, prior.ReviewedArtifactHash, true, ExecutionState.Queued);
        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveAsync(retry));
        Assert.Single(await repository.ListByRunIdAsync(RunId));
    }

    [Theory]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.Cancelled)]
    public async Task ExplicitRetryAfterKnownTerminalFailure_PreservesHistoryAndClosesBothSessions(ExecutionState state)
    {
        var channel = new DispatchSafetyChannel { Outcome = state };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(
            new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        var service = provider.GetRequiredService<IWorkflowReviewRequestService>();
        Assert.False((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        var repository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var first = Assert.Single(await repository.ListByRunIdAsync(RunId));
        Assert.Equal(state, first.ExecutionState);
        channel.Outcome = ExecutionState.Succeeded;
        Assert.False((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        var history = await repository.ListByRunIdAsync(RunId);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, channel.Calls);
        var retry = Assert.Single(history.Where(row => row.ExecutionId != first.ExecutionId));
        Assert.Equal(first.ReviewedArtifactHash, retry.ReviewedArtifactHash);
        Assert.Equal(state, (await repository.GetByExecutionIdAsync(first.ExecutionId))!.ExecutionState);
        var execution = await provider.GetRequiredService<IExecutionRepository>().GetByIdAsync(retry.ExecutionId);
        Assert.Equal(first.ExecutionId, execution!.RetryOfExecutionId);
        foreach (var row in history)
        {
            var session = await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(row.SessionId);
            Assert.Equal(SessionState.Closed, session!.State);
            Assert.Null(session.ActiveExecutionId);
        }
        Assert.True((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Equal(2, channel.Calls);
        Assert.Empty((await CurrentRunAsync()).Verdicts);
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Executions SET State='Succeeded' WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", first.ExecutionId);
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE Executions SET RetryOfExecutionId=$parent WHERE Id=$id;";
        command.Parameters["$id"].Value = retry.ExecutionId;
        command.Parameters.AddWithValue("$parent", first.ExecutionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync()); // Normal completion keeps lineage.
        command.Parameters["$parent"].Value = DBNull.Value;
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.Parameters["$parent"].Value = retry.ExecutionId;
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(first.ExecutionId,
            (await provider.GetRequiredService<IExecutionRepository>().GetByIdAsync(retry.ExecutionId))!.RetryOfExecutionId);
    }

    [Fact]
    public async Task UpgradeFrom16_RetainsBindingsAndAllIdentityObservationGuards()
    {
        using var legacy = new WorkflowReviewProvenanceTests(legacy: true);
        using var provider = legacy.CreateProvider();
        await legacy.StartRunAsync(provider, RealRouteId);
        var repository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var id = await legacy.PersistExecutionAsync(repository, ExecutionState.Failed, RealRouteId);
        var before = await repository.GetByExecutionIdAsync(id);
        var result = await new DatabaseMigrator(legacy._database.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 17).ToArray()).MigrateAsync();
        Assert.Equal(17, Assert.Single(result.AppliedMigrations).Version);
        var after = await repository.GetByExecutionIdAsync(id);
        Assert.Equal(before!.ExecutionId, after!.ExecutionId);
        Assert.Equal(before.ReviewedArtifactId, after.ReviewedArtifactId);
        Assert.Equal(before.ReviewedArtifactHash, after.ReviewedArtifactHash);
        Assert.Equal(before.ObservedRouteId, after.ObservedRouteId);
        Assert.Equal(before.ExecutionState, after.ExecutionState);
        await using var connection = await legacy._database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('TR_WorkflowReviewExecutions_IdentityIsComplete','TR_WorkflowReviewExecutions_BindingIsImmutable','TR_WorkflowReviewExecutions_ObservedRouteIsObserved','TR_WorkflowReviewExecutions_InsertObservedRouteIsObserved')";
        Assert.Equal(4L, await command.ExecuteScalarAsync());
        command.CommandText = "UPDATE WorkflowReviewExecutions SET ReviewedArtifactHash='forged' WHERE ExecutionId=$id";
        command.Parameters.AddWithValue("$id", id);
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE WorkflowReviewExecutions SET ObservedRouteId=$route WHERE ExecutionId=$id";
        command.Parameters.AddWithValue("$route", OtherRouteId);
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.Ambiguous)]
    [InlineData(ExecutionState.TimedOut)]
    [InlineData(ExecutionState.RouteMismatch)]
    [InlineData(ExecutionState.Succeeded)]
    public async Task UncertainActiveOrSuccessfulReview_StillRefusesReplay(ExecutionState state)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(
            new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await PersistExecutionAsync(provider.GetRequiredService<IWorkflowReviewEvidenceRepository>(), state, null);
        Assert.True((await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Equal(0, channel.Calls);
        Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        // Bypassing service preflight must not bypass the database admission rule.
        await Assert.ThrowsAsync<SqliteException>(() => PersistExecutionAsync(
            provider.GetRequiredService<IWorkflowReviewEvidenceRepository>(), ExecutionState.Queued, null));
    }
}
