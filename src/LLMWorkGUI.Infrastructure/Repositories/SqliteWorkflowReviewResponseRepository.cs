using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteWorkflowReviewResponseRepository(ISqliteConnectionFactory factory) : IWorkflowReviewResponseRepository
{
    public async Task<StoredWorkflowReviewResponse?> GetByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.NativeSessionId,r.ResponseText,r.ResponseSha256,r.ReceivedSha256,r.CapturedAtUtc,
                b.WorkflowRunId,b.StageId,b.ReviewerRole,b.ReviewedArtifactId,b.ReviewedArtifactHash
            FROM WorkflowReviewResponses r JOIN WorkflowReviewExecutions b ON b.ExecutionId=r.ExecutionId
            WHERE r.ExecutionId=$execution
            """;
        command.Parameters.AddWithValue("$execution", executionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var response = new WorkflowModelResponse(reader.GetString(1));
        if (response.Sha256 != reader.GetString(2)) throw new InvalidDataException("Stored reviewer response hash mismatch.");
        return new(executionId, reader.IsDBNull(0) ? null : reader.GetString(0), response,
            reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture))
        {
            Parsed = WorkflowReviewResponseParser.Parse(response, reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9))
        };
    }

    internal static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        string executionId, string? nativeSessionId, WorkflowModelResponse response, DateTimeOffset now, CancellationToken token)
    {
        // Hash both the received text and the exact redacted text retained at rest. Neither hash is origin proof.
        var stored = new WorkflowModelResponse(new SensitiveDataFilter().Redact(response.Content));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WorkflowReviewResponses(ExecutionId,NativeSessionId,ResponseText,ResponseSha256,ReceivedSha256,CapturedAtUtc)
            VALUES($execution,$native,$text,$hash,$received,$now)
            """;
        command.Parameters.AddWithValue("$execution", executionId);
        command.Parameters.AddWithValue("$native", (object?)nativeSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", stored.Content);
        command.Parameters.AddWithValue("$hash", stored.Sha256);
        command.Parameters.AddWithValue("$received", response.Sha256);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
