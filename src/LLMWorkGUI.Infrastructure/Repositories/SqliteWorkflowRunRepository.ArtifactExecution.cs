using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteWorkflowRunRepository
{
    public async Task ValidateArtifactExecutionAsync(WorkflowRun run, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ValidateArtifactExecutionAsync(connection, null, run, executionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateArtifactExecutionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        WorkflowRun run, string executionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
            JOIN WorkflowRuns w ON w.Id=$run
            WHERE e.Id=$execution AND e.State='Succeeded' AND e.EndedAtUtc IS NOT NULL
              AND e.FailureReason='None' AND s.WorkflowRunId=w.Id AND s.ProjectId=w.ProjectId
              AND w.ProjectId=$project AND julianday(e.EndedAtUtc)>=julianday(e.StartedAtUtc)
              AND julianday(e.StartedAtUtc)>=julianday(e.CreatedAtUtc)
              AND julianday(e.CreatedAtUtc)>=julianday(w.StartedAtUtc);
            """;
        command.Parameters.AddWithValue("$run", run.Id);
        command.Parameters.AddWithValue("$project", run.ProjectId);
        command.Parameters.AddWithValue("$execution", executionId);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
            throw new WorkflowValidationException("Artifact collection requires a stored successful execution owned by this run and project.");
    }
}
