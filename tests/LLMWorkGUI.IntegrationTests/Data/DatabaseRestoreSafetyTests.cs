using System.Security.Cryptography;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Data;

public sealed class DatabaseRestoreSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_DoesNotEvictAnotherDatabasePool(bool failVerification)
    {
        using var database = new TestDatabase();
        using var other = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await other.InitializeAsync();
        var backup = await new DatabaseBackupService(database.Factory)
            .CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "previous data");
        // A TEMP table belongs to a physical connection, so its survival verifies
        // that restoring one database did not evict the unrelated idle pool.
        await using (var connection = await other.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TEMP TABLE pool_marker (Value TEXT); INSERT INTO pool_marker VALUES ('retained');";
            await command.ExecuteNonQueryAsync();
        }

        if (failVerification)
        {
            var clock = new AfterCommitClock(database, backup.Sha256,
                () => throw new IOException("Synthetic verification failure."));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DatabaseBackupService(database.Factory, clock).RestoreAsync(backup.BackupPath));
            Assert.Equal("previous data", await ReadNameAsync(database));
        }
        else
        {
            await new DatabaseBackupService(database.Factory).RestoreAsync(backup.BackupPath);
            Assert.Equal("Test project", await ReadNameAsync(database));
        }

        await using var retained = await other.Factory.OpenConnectionAsync();
        await using var read = retained.CreateCommand();
        read.CommandText = "SELECT Value FROM pool_marker";
        Assert.Equal("retained", await read.ExecuteScalarAsync());
        AssertNoStaging(database);
    }

    [Fact]
    public async Task FixtureDisposal_DoesNotEvictAnotherDatabasePool()
    {
        using var other = new TestDatabase();
        await other.InitializeAsync();
        await using (var connection = await other.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TEMP TABLE pool_marker (Value TEXT); INSERT INTO pool_marker VALUES ('retained');";
            await command.ExecuteNonQueryAsync();
        }
        using (var disposed = new TestDatabase())
            await disposed.InitializeAsync();

        await using var retained = await other.Factory.OpenConnectionAsync();
        await using var read = retained.CreateCommand();
        read.CommandText = "SELECT Value FROM pool_marker";
        Assert.Equal("retained", await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Restore_PostCommitVerificationExceptionRestoresPreviousData()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "must survive verification failure");
        TestSqlitePool.Clear(database.Factory);
        var clock = new AfterCommitClock(database, backup.Sha256, () => throw new IOException("Synthetic verification I/O failure."));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DatabaseBackupService(database.Factory, clock).RestoreAsync(backup.BackupPath));

        Assert.True(clock.Invoked);
        Assert.Equal("must survive verification failure", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_ReplacementFailurePreservesCurrentDataAndCleansStage()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "must survive replacement failure");
        FileStream? lease = null;
        var factory = new CallbackFactory(database.Factory, () =>
            lease = new FileStream(database.Factory.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        try
        {
            var exception = await Record.ExceptionAsync(() => new DatabaseBackupService(factory).RestoreAsync(backup.BackupPath));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.True(factory.CallbackInvoked);
        Assert.Equal("must survive replacement failure", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_ToMissingDatabasePublishesVerifiedSnapshotWithoutRollback()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        var factory = new SqliteConnectionFactory(Path.Combine(database.Root, "new.db"));
        var service = new DatabaseBackupService(factory);

        var result = await service.RestoreAsync(backup.BackupPath);

        Assert.Null(result.RollbackPath);
        Assert.Equal(backup.Sha256, result.Sha256);
        Assert.True((await service.VerifyIntegrityAsync()).IsHealthy);
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Projects WHERE Id = 'project-1';";
        Assert.Equal("Test project", await command.ExecuteScalarAsync());
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_UsesVerifiedSnapshotWhenOriginalBackupChangesBeforeReplacement()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "keep until restore");
        var replacement = await service.CreateBackupAsync(Path.Combine(database.Root, "replacement.db"));
        var factory = new CallbackFactory(database.Factory, () => File.Copy(replacement.BackupPath, backup.BackupPath, true));

        var result = await new DatabaseBackupService(factory).RestoreAsync(backup.BackupPath);

        Assert.True(factory.CallbackInvoked);
        Assert.NotEqual(backup.Sha256, Hash(backup.BackupPath));
        Assert.Equal(backup.Sha256, result.Sha256);
        Assert.Equal("Test project", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_CancellationAfterCommitDoesNotReportAnUnfinishedRestore()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "keep until restore");
        TestSqlitePool.Clear(database.Factory);
        using var cancellation = new CancellationTokenSource();
        var clock = new AfterCommitClock(database, backup.Sha256, cancellation.Cancel);

        var result = await new DatabaseBackupService(database.Factory, clock).RestoreAsync(backup.BackupPath, cancellation.Token);

        Assert.True(clock.Invoked);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(backup.Sha256, result.Sha256);
        Assert.Equal("Test project", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_FailedPostCommitIntegrityRollsBackEvenWhenCallerCancels()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "must survive rollback");
        TestSqlitePool.Clear(database.Factory);
        using var cancellation = new CancellationTokenSource();
        var clock = new AfterCommitClock(database, backup.Sha256, () =>
        {
            File.WriteAllBytes(database.Factory.DatabasePath, new byte[64]);
            cancellation.Cancel();
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DatabaseBackupService(database.Factory, clock).RestoreAsync(backup.BackupPath, cancellation.Token));

        Assert.True(clock.Invoked);
        Assert.Equal("must survive rollback", await ReadNameAsync(database));
        Assert.True((await new DatabaseBackupService(database.Factory).VerifyIntegrityAsync()).IsHealthy);
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_CancellationBeforeCommitPreservesCurrentData()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var backup = await new DatabaseBackupService(database.Factory).CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await SetNameAsync(database, "must survive cancellation");
        using var cancellation = new CancellationTokenSource();
        var factory = new CallbackFactory(database.Factory, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DatabaseBackupService(factory).RestoreAsync(backup.BackupPath, cancellation.Token));

        Assert.Equal("must survive cancellation", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    [Fact]
    public async Task Restore_WithAnOpenWalConnectionRefusesWithoutLosingCommittedData()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "backup.db"));
        await using var live = await database.Factory.OpenConnectionAsync();
        await using (var command = live.CreateCommand())
        {
            command.CommandText = "UPDATE Projects SET DisplayName = 'committed in WAL' WHERE Id = 'project-1';";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(File.Exists(database.Factory.DatabasePath + "-wal"));

        var exception = await Record.ExceptionAsync(() => service.RestoreAsync(backup.BackupPath));
        Assert.True(exception is IOException or UnauthorizedAccessException,
            $"Expected a safe filesystem refusal, observed {exception?.GetType().Name ?? "success"}.");

        Assert.Equal("committed in WAL", await ReadNameAsync(database));
        await live.CloseAsync();
        TestSqlitePool.Clear(database.Factory);
        Assert.Equal("committed in WAL", await ReadNameAsync(database));
        AssertNoStaging(database);
    }

    private static async Task SetNameAsync(TestDatabase database, string name)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Projects SET DisplayName = $name WHERE Id = 'project-1';";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadNameAsync(TestDatabase database)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Projects WHERE Id = 'project-1';";
        return await command.ExecuteScalarAsync() as string;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void AssertNoStaging(TestDatabase database) =>
        Assert.Empty(Directory.GetFiles(database.Root, "*.restore*", SearchOption.AllDirectories));

    private sealed class AfterCommitClock(TestDatabase database, string backupHash, Action callback) : TimeProvider
    {
        public bool Invoked { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (!Invoked && Directory.GetFiles(database.Root, "*.rollback-*").Length > 0
                && Hash(database.Factory.DatabasePath) == backupHash)
            {
                Invoked = true;
                callback();
            }
            return DateTimeOffset.UnixEpoch;
        }
    }

    private sealed class CallbackFactory(ISqliteConnectionFactory inner, Action callback) : ISqliteConnectionFactory
    {
        public bool CallbackInvoked { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection() => inner.CreateConnection();
        public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (!CallbackInvoked)
            {
                CallbackInvoked = true;
                callback();
            }
            return inner.OpenConnectionAsync(cancellationToken);
        }
    }
}
