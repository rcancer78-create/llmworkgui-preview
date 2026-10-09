using System.Text.Json;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

[Collection("Activity storage isolation")]
public sealed partial class QuotaDiagnosticStorageTests
{
    private const string Secret = "synthetic-storage-credential";
    private static readonly DateTimeOffset Captured = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DirectSaveSanitizesErrorAtRestAndPreservesQuotaMetadata()
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(CreateSnapshot());
        AssertSafe(await ReadPayloadAsync(database.Factory));
        AssertSnapshot(await repository.GetByIdAsync("quota-storage"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("account")]
    [InlineData("account-list")]
    [InlineData("all-list")]
    public async Task EveryRepositoryReadSanitizesLegacyDiagnostics(string readPath)
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await SeedLegacyAsync(database.Factory, repository);
        QuotaSnapshot? result = readPath switch
        {
            "id" => await repository.GetByIdAsync("quota-storage"),
            "account" => await repository.GetLatestForAccountAsync("account-1", "model-1"),
            "account-list" => Assert.Single(await repository.ListLatestByAccountIdAsync("account-1")),
            _ => Assert.Single(await repository.ListAllLatestAsync())
        };
        AssertSnapshot(result);
    }

    [Fact]
    public async Task StartupSanitizesLegacyRowsBeforeTheyReachTheUi()
    {
        using var database = await CreateDatabaseAsync();
        await SeedLegacyAsync(database.Factory, new SqliteQuotaSnapshotRepository(database.Factory));
        using var host = HostBootstrapper.BuildHost(appDataDirectory: database.Root);
        await HostBootstrapper.InitializeAsync(host);
        AssertSafe(await ReadPayloadAsync(database.Factory));
        AssertSnapshot(await new SqliteQuotaSnapshotRepository(database.Factory).GetByIdAsync("quota-storage"));
    }

    [Fact]
    public async Task StartupSanitizesMultipleBatchesAndLeavesCleanPayloadBytesIntact()
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await SeedLegacyAsync(database.Factory, repository);
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<205)
                INSERT INTO QuotaSnapshots (Id, AccountId, Freshness, Source, CapturedAtUtc, RawRedactedPayloadJson)
                SELECT 'batch-'||x, 'account-1', 'ExactProviderReported', 'provider-1', $captured, RawRedactedPayloadJson
                FROM n, QuotaSnapshots WHERE Id='quota-storage';
                INSERT INTO QuotaSnapshots (Id, AccountId, Freshness, Source, CapturedAtUtc, RawRedactedPayloadJson)
                VALUES ('clean', 'account-1', 'ExactProviderReported', 'provider-1', $captured, $clean);
                INSERT INTO QuotaSnapshots (Id, AccountId, Freshness, Source, CapturedAtUtc)
                VALUES ('null-payload', 'account-1', 'ExactProviderReported', 'provider-1', $captured);
                """;
            command.Parameters.AddWithValue("$captured", Captured.ToString("O"));
            command.Parameters.AddWithValue("$clean", "  { \"errorMessage\":\"ordinary\", \"buckets\":[] }  ");
            await command.ExecuteNonQueryAsync();
        }
        using var host = HostBootstrapper.BuildHost(appDataDirectory: database.Root);
        await HostBootstrapper.InitializeAsync(host);
        await using var checkConnection = await database.Factory.OpenConnectionAsync();
        await using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT RawRedactedPayloadJson FROM QuotaSnapshots ORDER BY Id;";
        await using var reader = await check.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0)) { count++; continue; }
            var payload = reader.GetString(0);
            if (payload.Contains("ordinary", StringComparison.Ordinal))
            {
                Assert.Equal("  { \"errorMessage\":\"ordinary\", \"buckets\":[] }  ", payload);
            }
            else { AssertSafe(payload); }
            count++;
        }
        Assert.Equal(208, count);
    }

    [Fact]
    public async Task TransientMaintenanceFailureAllowsStartupWithoutLoggingProviderText()
    {
        using var database = await CreateDatabaseAsync();
        await SeedLegacyAsync(database.Factory, new SqliteQuotaSnapshotRepository(database.Factory));
        var original = await ReadPayloadAsync(database.Factory);
        using var logs = new CaptureLogProvider();
        using var host = new HostBuilder().ConfigureLogging(builder => builder.AddProvider(logs))
            .ConfigureServices(services =>
            {
                services.AddSingleton(new DatabaseMigrator(database.Factory));
                services.AddSingleton<ISqliteConnectionFactory>(new FailingMaintenanceFactory(database.Factory));
            }).Build();
        await HostBootstrapper.InitializeAsync(host);
        Assert.Equal(original, await ReadPayloadAsync(database.Factory));
        Assert.Contains(logs.Messages, message => message.Contains("maintenance failed", StringComparison.Ordinal));
        Assert.All(logs.Messages, message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
    }

    [Fact]
    public async Task RestoreNormalizesCleanWalSnapshotAndKeepsEveryCommittedRow()
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(CreateSnapshot());
        var sourcePayload = await ReadPayloadAsync(database.Factory);
        var backup = await new DatabaseBackupService(database.Factory)
            .CreateBackupAsync(Path.Combine(database.Root, "clean-wal.db"));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup.BackupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", await journal.ExecuteScalarAsync());
        }
        var ownerBytes = await File.ReadAllBytesAsync(backup.BackupPath);
        var ownerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ownerBytes));
        await File.WriteAllTextAsync(backup.ChecksumPath, ownerHash);
        TestSqlitePool.Clear(database.Factory);
        var restored = await new DatabaseBackupService(database.Factory).RestoreAsync(backup.BackupPath);
        Assert.NotEqual(ownerHash, restored.Sha256);
        Assert.Equal(sourcePayload, await ReadPayloadAsync(database.Factory));
        AssertSnapshot(await repository.GetByIdAsync("quota-storage"));
        Assert.Equal(ownerBytes, await File.ReadAllBytesAsync(backup.BackupPath));
    }

    [Fact]
    public async Task CancelledMaintenanceDoesNotModifyLegacyPayload()
    {
        using var database = await CreateDatabaseAsync();
        await SeedLegacyAsync(database.Factory, new SqliteQuotaSnapshotRepository(database.Factory));
        var original = await ReadPayloadAsync(database.Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var connection = await database.Factory.OpenConnectionAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QuotaDiagnosticMaintenance.RedactLegacyPayloadsAsync(connection, cancellation.Token));
        Assert.Equal(original, await ReadPayloadAsync(database.Factory));
    }

    [Fact]
    public async Task BackupSanitizesLegacyRowsInThePublishedSnapshot()
    {
        using var database = await CreateDatabaseAsync();
        await SeedLegacyAsync(database.Factory, new SqliteQuotaSnapshotRepository(database.Factory));
        var sourcePayload = await ReadPayloadAsync(database.Factory);
        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "quota-backup.db"));
        Assert.True(backup.IntegrityOk);
        var factory = new ReadOnlySnapshotFactory(backup.BackupPath);
        AssertSafe(await ReadPayloadAsync(factory));
        AssertSnapshot(await new SqliteQuotaSnapshotRepository(factory).GetByIdAsync("quota-storage"));
        Assert.True((await service.VerifyIntegrityAsync(backup.BackupPath)).ChecksumMatches);
        Assert.Equal(sourcePayload, await ReadPayloadAsync(database.Factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreSanitizesVerifiedLegacyCandidateWithoutModifyingOwnersBackup(bool walMode)
    {
        using var database = await CreateDatabaseAsync();
        await SeedLegacyAsync(database.Factory, new SqliteQuotaSnapshotRepository(database.Factory));
        var service = new DatabaseBackupService(database.Factory);
        var legacyPath = Path.Combine(database.Root, "legacy-owner-backup.db");
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $destination;";
            command.Parameters.AddWithValue("$destination", legacyPath);
            await command.ExecuteNonQueryAsync();
        }
        if (walMode)
        {
            await using var ownerConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await ownerConnection.OpenAsync();
            await using var journal = ownerConnection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", await journal.ExecuteScalarAsync());
        }
        var beforeBytes = await File.ReadAllBytesAsync(legacyPath);
        var beforeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(beforeBytes));
        await File.WriteAllTextAsync(legacyPath + ".sha256", beforeHash);
        TestSqlitePool.Clear(database.Factory);
        var restored = await service.RestoreAsync(legacyPath);
        Assert.NotEqual(beforeHash, restored.Sha256);
        Assert.Equal(restored.Sha256, (await service.VerifyIntegrityAsync()).Sha256);
        AssertSafe(await ReadPayloadAsync(database.Factory));
        AssertSnapshot(await new SqliteQuotaSnapshotRepository(database.Factory).GetByIdAsync("quota-storage"));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(legacyPath));
    }

    [Theory]
    [InlineData("{\"api_key\":\"synthetic-storage-credential\",\"code\":401}")]
    [InlineData("[{\"api_key\":\"synthetic-storage-credential\"},42]")]
    [InlineData("42")]
    public async Task LegacyNestedDiagnosticValuesRemainValidJsonAndKeepQuotaMetadata(string extra)
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await SeedLegacyAsync(database.Factory, repository);
        var original = await ReadPayloadAsync(database.Factory);
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='quota-storage';";
            update.Parameters.AddWithValue("$payload", original[..^1] + ",\"extra\":" + extra + "}");
            await update.ExecuteNonQueryAsync();
        }
        var snapshot = await repository.GetByIdAsync("quota-storage");
        AssertSnapshot(snapshot);
        using var result = JsonDocument.Parse(snapshot!.RawRedactedPayloadJson!);
        var value = result.RootElement.GetProperty("extra");
        if (value.ValueKind == JsonValueKind.Object) { Assert.Equal(401, value.GetProperty("code").GetInt32()); }
        if (value.ValueKind == JsonValueKind.Array) { Assert.Equal(42, value[1].GetInt32()); }
        if (value.ValueKind == JsonValueKind.Number) { Assert.Equal(42, value.GetInt32()); }
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        return database;
    }

    private static QuotaSnapshot CreateSnapshot() => new("quota-storage", "account-1",
        QuotaProvenance.ExactProviderReported, Captured,
        new[] { new QuotaBucket("token_count", QuotaLimitUnit.Tokens, QuotaLimitWindow.PerDay,
            100, 25, 75, Captured.AddDays(1), 5, QuotaConfidence.Exact) },
        "provider-1", "model-1", Captured.AddMinutes(5), errorMessage: "{\"api_key\":\"" + Secret + "\",\"message\":\"password=a\"}");

    private static async Task SeedLegacyAsync(ISqliteConnectionFactory factory, SqliteQuotaSnapshotRepository repository)
    {
        await repository.SaveAsync(CreateSnapshot());
        var payload = JsonSerializer.Serialize(new
        {
            errorMessage = CreateSnapshot().ErrorMessage,
            expiresAtUtc = Captured.AddMinutes(5).ToString("O"),
            buckets = new[] { new { name = "token_count", unit = "Tokens", window = "PerDay",
                limit = 100, used = 25, remaining = 75, resetAt = Captured.AddDays(1).ToString("O"), hardReserve = 5, confidence = "Exact" } },
            api_key = Secret
        });
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='quota-storage';";
        command.Parameters.AddWithValue("$payload", payload);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> ReadPayloadAsync(ISqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RawRedactedPayloadJson FROM QuotaSnapshots WHERE Id='quota-storage';";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static void AssertSnapshot(QuotaSnapshot? snapshot)
    {
        Assert.NotNull(snapshot);
        Assert.Equal("quota-storage", snapshot.Id);
        Assert.Equal("account-1", snapshot.AccountId);
        Assert.Equal("model-1", snapshot.ModelId);
        Assert.Equal("provider-1", snapshot.ProviderProfileId);
        Assert.Equal(QuotaProvenance.ExactProviderReported, snapshot.Provenance);
        Assert.Equal(Captured, snapshot.CapturedAt);
        Assert.Equal(Captured.AddMinutes(5), snapshot.ExpiresAt);
        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal("token_count", bucket.BucketName);
        Assert.Equal(100, bucket.LimitValue);
        Assert.Equal(25, bucket.UsedValue);
        Assert.Equal(75, bucket.RemainingValue);
        Assert.Equal(5, bucket.HardReserve);
        Assert.Equal(Captured.AddDays(1), bucket.ResetAt);
        AssertSafe(snapshot.RawRedactedPayloadJson!);
        AssertSafe(snapshot.ErrorMessage!);
    }

    private static void AssertSafe(string text)
    {
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("password=a", text, StringComparison.Ordinal);
        Assert.Contains("REDACTED", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
    }

    private sealed class ReadOnlySnapshotFactory(string path) : ISqliteConnectionFactory
    {
        public string DatabasePath => path;
        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString();
        public SqliteConnection CreateConnection() => new(ConnectionString);
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            try { await connection.OpenAsync(cancellationToken); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }
    }

    private sealed class FailingMaintenanceFactory(ISqliteConnectionFactory inner) : ISqliteConnectionFactory
    {
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection() => inner.CreateConnection();
        public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqliteException("synthetic failure " + Secret, 5);
        }
    }

    private sealed class CaptureLogProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public List<Exception?> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void Dispose() { }
        private sealed class CaptureLogger(CaptureLogProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Messages.Add(formatter(state, exception));
                owner.Exceptions.Add(exception);
            }
        }
    }
}
