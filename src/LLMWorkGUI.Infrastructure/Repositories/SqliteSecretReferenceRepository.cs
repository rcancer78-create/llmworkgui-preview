using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// Reference-only secret metadata over the <c>SecretReferences</c> and <c>SecretReferenceOwners</c>
/// tables (ADR-0005 §2.1). No statement here can read or write a secret value: the only identifier
/// that ever reaches SQL is the URN.
/// </summary>
public sealed class SqliteSecretReferenceRepository : ISecretReferenceRepository
{
    private const string SelectColumns = """
        Reference, Kind, State, CreatedAtUtc, LastRotatedAtUtc, RevokedAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteSecretReferenceRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<SecretReferenceMetadata?> GetAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM SecretReferences WHERE Reference = $reference;";
        command.Parameters.AddWithValue("$reference", reference);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMetadata(reader) : null;
    }

    public async Task<IReadOnlyList<SecretReferenceMetadata>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM SecretReferences ORDER BY Reference;";

        var list = new List<SecretReferenceMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadMetadata(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<SecretReferenceOwnerBinding>> ListOwnersAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Reference, OwnerKind, OwnerId
            FROM SecretReferenceOwners
            WHERE Reference = $reference
            ORDER BY OwnerKind, OwnerId;
            """;
        command.Parameters.AddWithValue("$reference", reference);

        var list = new List<SecretReferenceOwnerBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new SecretReferenceOwnerBinding
            {
                Reference = reader.GetString(0),
                OwnerKind = SqliteRepositorySupport.ParseEnum<SecretReferenceOwnerKind>(reader.GetString(1)),
                OwnerId = reader.GetString(2)
            });
        }

        return list;
    }

    public async Task InsertAsync(SecretReferenceMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SecretReferences
                (Reference, Kind, State, CreatedAtUtc, LastRotatedAtUtc, RevokedAtUtc)
            VALUES
                ($reference, $kind, $state, $createdAtUtc, $lastRotatedAtUtc, $revokedAtUtc);
            """;

        command.Parameters.AddWithValue("$reference", metadata.Reference);
        command.Parameters.AddWithValue("$kind", SqliteRepositorySupport.FormatEnum(metadata.Kind));
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(metadata.State));
        command.Parameters.AddWithValue("$createdAtUtc", SqliteRepositorySupport.FormatTimestamp(metadata.CreatedAtUtc));
        SqliteRepositorySupport.AddNullable(
            command,
            "$lastRotatedAtUtc",
            metadata.LastRotatedAtUtc.HasValue
                ? SqliteRepositorySupport.FormatTimestamp(metadata.LastRotatedAtUtc.Value)
                : null);
        SqliteRepositorySupport.AddNullable(
            command,
            "$revokedAtUtc",
            metadata.RevokedAtUtc.HasValue
                ? SqliteRepositorySupport.FormatTimestamp(metadata.RevokedAtUtc.Value)
                : null);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkRotatedAsync(
        string reference,
        DateTimeOffset lastRotatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SecretReferences SET LastRotatedAtUtc = $lastRotatedAtUtc WHERE Reference = $reference;";
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$lastRotatedAtUtc", SqliteRepositorySupport.FormatTimestamp(lastRotatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateStateAsync(
        string reference,
        SecretReferenceState state,
        DateTimeOffset? revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SecretReferences
            SET State = $state, RevokedAtUtc = $revokedAtUtc
            WHERE Reference = $reference;
            """;
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(state));
        SqliteRepositorySupport.AddNullable(
            command,
            "$revokedAtUtc",
            revokedAtUtc.HasValue ? SqliteRepositorySupport.FormatTimestamp(revokedAtUtc.Value) : null);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddOwnerAsync(SecretReferenceOwnerBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
            VALUES ($reference, $ownerKind, $ownerId);
            """;
        command.Parameters.AddWithValue("$reference", binding.Reference);
        command.Parameters.AddWithValue("$ownerKind", SqliteRepositorySupport.FormatEnum(binding.OwnerKind));
        command.Parameters.AddWithValue("$ownerId", binding.OwnerId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SecretReferences WHERE Reference = $reference;";
        command.Parameters.AddWithValue("$reference", reference);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static SecretReferenceMetadata ReadMetadata(SqliteDataReader reader)
    {
        return new SecretReferenceMetadata
        {
            Reference = reader.GetString(0),
            Kind = SqliteRepositorySupport.ParseEnum<SecretReferenceKind>(reader.GetString(1)),
            State = SqliteRepositorySupport.ParseEnum<SecretReferenceState>(reader.GetString(2)),
            CreatedAtUtc = SqliteRepositorySupport.ParseTimestamp(reader.GetString(3)),
            LastRotatedAtUtc = SqliteRepositorySupport.GetNullableTimestamp(reader, 4),
            RevokedAtUtc = SqliteRepositorySupport.GetNullableTimestamp(reader, 5)
        };
    }
}
