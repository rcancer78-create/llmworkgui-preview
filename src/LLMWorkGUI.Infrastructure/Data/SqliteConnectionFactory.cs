using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Data;

public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }

    string ConnectionString { get; }

    SqliteConnection CreateConnection();

    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(ConnectionString);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            await foreignKeys.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL;";
            await journalMode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
