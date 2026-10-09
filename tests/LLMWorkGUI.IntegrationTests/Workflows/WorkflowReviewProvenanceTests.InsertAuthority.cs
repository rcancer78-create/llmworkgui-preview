using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Fact]
    public async Task DirectBindingInsertCannotForgeAnObservationMissingFromTheExecution()
    {
        using var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var store = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(store, ExecutionState.Queued, null);
        await using var connection = await _database.Factory.OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        await using var preparation = connection.CreateCommand();
        preparation.Transaction = transaction;
        preparation.CommandText = """
            CREATE TEMP TABLE InsertionCandidate AS SELECT * FROM WorkflowReviewExecutions WHERE ExecutionId=$execution;
            DELETE FROM WorkflowReviewExecutions WHERE ExecutionId=$execution;
            """;
        preparation.Parameters.AddWithValue("$execution", executionId);
        await preparation.ExecuteNonQueryAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO WorkflowReviewExecutions
                (Id,ExecutionId,SessionId,WorkflowRunId,ReviewerRole,StageId,ReviewedArtifactId,
                 ReviewedArtifactHash,IsReadOnly,RequestedRouteId,ObservedRouteId,RequestedAtUtc,UpdatedAtUtc)
            SELECT Id,ExecutionId,SessionId,WorkflowRunId,ReviewerRole,StageId,ReviewedArtifactId,
                   ReviewedArtifactHash,IsReadOnly,RequestedRouteId,RequestedRouteId,RequestedAtUtc,UpdatedAtUtc
            FROM InsertionCandidate;
            """;

        await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
    }
}
