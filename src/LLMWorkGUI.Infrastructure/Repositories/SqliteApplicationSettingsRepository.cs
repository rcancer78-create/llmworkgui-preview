using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Data;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteApplicationSettingsRepository : IApplicationSettingsRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteApplicationSettingsRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM ApplicationSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string text ? text : null;
    }

    public async Task SetValueAsync(
        string key,
        string value,
        string valueType = "String",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueType);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApplicationSettings (Key, Value, ValueType, UpdatedAtUtc)
            VALUES ($key, $value, $valueType, $updatedAtUtc)
            ON CONFLICT (Key) DO UPDATE SET
                Value = excluded.Value,
                ValueType = excluded.ValueType,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$valueType", valueType);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ApplicationSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM ApplicationSettings ORDER BY Key;";

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }
}
