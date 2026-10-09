using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteApprovalRuleRepository : IApprovalRuleRepository
{
    private const string SelectColumns = """
        Id, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IApplicationInstanceGuard? _instanceGuard;

    public SqliteApprovalRuleRepository(ISqliteConnectionFactory connectionFactory, IApplicationInstanceGuard? instanceGuard = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _instanceGuard = instanceGuard;
    }

    public async Task<IReadOnlyList<ApprovalRule>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM ApprovalRules ORDER BY CreatedAtUtc DESC, Id;";

        var list = new List<ApprovalRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRule(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<ApprovalRule>> ListByProjectIdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM ApprovalRules WHERE ProjectId = $projectId ORDER BY CreatedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var list = new List<ApprovalRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRule(reader));
        }

        return list;
    }

    public async Task<ApprovalRule?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM ApprovalRules WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRule(reader) : null;
    }

    public async Task UpsertAsync(ApprovalRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _instanceGuard?.EnsureSupervisorPermitted();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApprovalRules
                (Id, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc)
            VALUES
                ($id, $backend, $providerProfileId, $projectId, $pathScope, $kind, $operation, $expiresAtUtc, $createdBy, $createdAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                Backend = excluded.Backend,
                ProviderProfileId = excluded.ProviderProfileId,
                ProjectId = excluded.ProjectId,
                PathScope = excluded.PathScope,
                Kind = excluded.Kind,
                Operation = excluded.Operation,
                ExpiresAtUtc = excluded.ExpiresAtUtc,
                CreatedBy = excluded.CreatedBy;
            """;

        command.Parameters.AddWithValue("$id", rule.Id);
        command.Parameters.AddWithValue("$backend", SqliteRepositorySupport.FormatEnum(rule.Backend));
        command.Parameters.AddWithValue("$providerProfileId", rule.ProviderProfileId);
        command.Parameters.AddWithValue("$projectId", rule.ProjectId);
        SqliteRepositorySupport.AddNullable(command, "$pathScope", rule.PathScope);
        command.Parameters.AddWithValue("$kind", SqliteRepositorySupport.FormatEnum(rule.Kind));
        command.Parameters.AddWithValue("$operation", rule.Operation);
        SqliteRepositorySupport.AddNullable(command, "$expiresAtUtc", rule.ExpiresAt.HasValue ? SqliteRepositorySupport.FormatTimestamp(rule.ExpiresAt.Value) : null);
        command.Parameters.AddWithValue("$createdBy", rule.CreatedBy);
        command.Parameters.AddWithValue("$createdAtUtc", SqliteRepositorySupport.FormatTimestamp(rule.CreatedAt));

        _instanceGuard?.EnsureSupervisorPermitted();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _instanceGuard?.EnsureSupervisorPermitted();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ApprovalRules WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        _instanceGuard?.EnsureSupervisorPermitted();
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static ApprovalRule ReadRule(SqliteDataReader reader)
    {
        var expiresAtStr = SqliteRepositorySupport.GetNullableString(reader, 7);
        DateTimeOffset? expiresAt = expiresAtStr != null ? SqliteRepositorySupport.ParseTimestamp(expiresAtStr) : null;

        return new ApprovalRule(
            reader.GetString(0),
            SqliteRepositorySupport.ParseEnum<BackendType>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            SqliteRepositorySupport.GetNullableString(reader, 4),
            SqliteRepositorySupport.ParseEnum<NormalizedApprovalKind>(reader.GetString(5)),
            reader.GetString(6),
            expiresAt,
            reader.GetString(8),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(9)));
    }
}
