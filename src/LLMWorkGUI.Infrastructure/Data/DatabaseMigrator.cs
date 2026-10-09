using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Data;

public sealed class DatabaseMigrator
{
    public const string MigrationsResourcePrefix = "LLMWorkGUI.Infrastructure.Data.Migrations.";

    private const string SqlExtension = ".sql";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public DatabaseMigrator(
        ISqliteConnectionFactory connectionFactory,
        IReadOnlyList<DatabaseMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        Migrations = migrations ?? LoadEmbeddedMigrations();
        ValidateMigrations(Migrations);
    }

    public IReadOnlyList<DatabaseMigration> Migrations { get; }

    public static IReadOnlyList<DatabaseMigration> LoadEmbeddedMigrations(Assembly? assembly = null)
    {
        assembly ??= typeof(DatabaseMigrator).Assembly;

        var migrations = new List<DatabaseMigration>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(MigrationsResourcePrefix, StringComparison.Ordinal)
                                    && name.EndsWith(SqlExtension, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var shortName = resourceName[MigrationsResourcePrefix.Length..^SqlExtension.Length];
            var separatorIndex = shortName.IndexOf('_');

            if (separatorIndex <= 0
                || !int.TryParse(
                    shortName[..separatorIndex],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var version))
            {
                throw new InvalidOperationException(
                    $"Embedded migration resource '{resourceName}' must be named '<version>_<name>.sql'.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded migration resource '{resourceName}' could not be opened.");

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            migrations.Add(new DatabaseMigration(
                version,
                shortName[(separatorIndex + 1)..],
                reader.ReadToEnd()));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No embedded database migrations were found.");
        }

        return migrations;
    }

    /// <summary>Checks a secondary instance's schema without creating or migrating the database.</summary>
    public async Task ValidateCurrentSchemaAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionFactory.ConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (applied.Count != Migrations.Count || Migrations.Any(migration =>
            !applied.TryGetValue(migration.Version, out var checksum)
            || !string.Equals(checksum, migration.Checksum, StringComparison.Ordinal)))
            throw new InvalidOperationException("A view-only instance requires the current verified database schema; restart after the primary instance finishes upgrading.");
    }

    public async Task<MigrationReport> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsureMigrationsTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var appliedMigrations = await ReadAppliedMigrationsAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        var highestKnownVersion = Migrations.Max(migration => migration.Version);
        var databaseVersion = appliedMigrations.Count == 0 ? 0 : appliedMigrations.Keys.Max();

        if (databaseVersion > highestKnownVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {databaseVersion} is newer than the highest known migration version {highestKnownVersion}.");
        }

        foreach (var migration in Migrations)
        {
            if (appliedMigrations.TryGetValue(migration.Version, out var checksum)
                && !string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration {migration.Version} ('{migration.Name}') was already applied with a different checksum.");
            }
        }

        // Known checksums alone are insufficient: replaying a missing older migration after a
        // newer one can execute DDL against the wrong schema. Sparse version numbers are valid,
        // but the recorded set must be an exact prefix of this application's ordered history.
        var orderedMigrations = Migrations.OrderBy(migration => migration.Version).ToArray();
        var knownVersions = orderedMigrations.Select(migration => migration.Version).ToHashSet();
        if (appliedMigrations.Keys.Any(version => !knownVersions.Contains(version))
            || orderedMigrations.Take(appliedMigrations.Count)
                .Any(migration => !appliedMigrations.ContainsKey(migration.Version)))
        {
            throw new InvalidOperationException(
                "Database migration history is not an ordered prefix of the known migrations.");
        }

        var pendingMigrations = Migrations
            .Where(migration => !appliedMigrations.ContainsKey(migration.Version))
            .OrderBy(migration => migration.Version)
            .ToArray();

        var previousSchemaVersion = appliedMigrations.Count == 0
            ? 0
            : appliedMigrations.Keys.Max();

        foreach (var migration in pendingMigrations)
        {
            await ApplyMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        }

        var currentSchemaVersion = pendingMigrations.Length == 0
            ? previousSchemaVersion
            : pendingMigrations[^1].Version;

        return new MigrationReport(previousSchemaVersion, currentSchemaVersion, pendingMigrations);
    }

    private static async Task EnsureMigrationsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DatabaseSchema.MigrationsTableName} (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                checksum TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var appliedMigrations = new Dictionary<int, string>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT version, checksum FROM {DatabaseSchema.MigrationsTableName};";

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            appliedMigrations[reader.GetInt32(0)] = reader.GetString(1);
        }

        return appliedMigrations;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.Transaction = transaction;
            schemaCommand.CommandText = migration.Sql;

            await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.Transaction = transaction;
            historyCommand.CommandText = $"""
                INSERT INTO {DatabaseSchema.MigrationsTableName} (version, name, checksum, applied_at_utc)
                VALUES ($version, $name, $checksum, $appliedAtUtc);
                """;
            historyCommand.Parameters.AddWithValue("$version", migration.Version);
            historyCommand.Parameters.AddWithValue("$name", migration.Name);
            historyCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
            historyCommand.Parameters.AddWithValue(
                "$appliedAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static void ValidateMigrations(IReadOnlyList<DatabaseMigration> migrations)
    {
        var versions = new HashSet<int>();

        foreach (var migration in migrations)
        {
            if (!versions.Add(migration.Version))
            {
                throw new ArgumentException(
                    $"Duplicate migration version {migration.Version}.",
                    nameof(migrations));
            }
        }
    }
}
