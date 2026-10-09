using System.Security.Cryptography;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Data;

public sealed class DatabaseBackupServiceTests
{
    [Fact]
    public async Task CreateBackup_ProducesConsistentSnapshotWithChecksumAndIntegrity()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();
        await database.SeedRouteChainAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backupPath = Path.Combine(database.Root, "backups", "snapshot.db");

        var result = await service.CreateBackupAsync(backupPath);

        Assert.True(File.Exists(result.BackupPath));
        Assert.True(File.Exists(result.ChecksumPath));
        Assert.True(result.IntegrityOk);
        Assert.True(result.SchemaVersion >= 1);
        Assert.True(result.SizeBytes > 0);
        Assert.Equal(result.Sha256, await File.ReadAllTextAsync(result.ChecksumPath));

        var integrity = await service.VerifyIntegrityAsync(result.BackupPath);

        Assert.True(integrity.IsHealthy);
        Assert.True(integrity.ChecksumMatches);
        Assert.Equal(result.SchemaVersion, integrity.SchemaVersion);
        Assert.Single(integrity.Messages);
        Assert.Equal("ok", integrity.Messages[0]);
    }

    [Fact]
    public async Task CreateBackup_WithoutDestinationWritesIntoTheManagedBackupsDirectory()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();

        var service = new DatabaseBackupService(database.Factory);

        var result = await service.CreateBackupAsync();

        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains("backups", result.BackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IntegrityOk);
    }

    [Fact]
    public async Task CreateBackup_RefusesToOverwriteAnExistingDestination()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backupPath = Path.Combine(database.Root, "backup.db");

        await service.CreateBackupAsync(backupPath);

        await Assert.ThrowsAsync<IOException>(() => service.CreateBackupAsync(backupPath));
    }

    [Fact]
    public async Task VerifyIntegrity_ReportsCorruptionInsteadOfThrowing()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backupPath = Path.Combine(database.Root, "corrupted.db");
        var result = await service.CreateBackupAsync(backupPath);

        await File.WriteAllBytesAsync(result.BackupPath, new byte[64]);

        var integrity = await service.VerifyIntegrityAsync(result.BackupPath);

        Assert.False(integrity.IsHealthy);
        Assert.False(integrity.ChecksumMatches);
        Assert.NotEmpty(integrity.Messages);
    }

    [Fact]
    public async Task VerifyIntegrity_WithoutAChecksumSidecarReportsAnUnknownChecksum()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backupPath = Path.Combine(database.Root, "no-sidecar.db");
        var result = await service.CreateBackupAsync(backupPath);

        File.Delete(result.ChecksumPath);

        var integrity = await service.VerifyIntegrityAsync(result.BackupPath);

        Assert.True(integrity.IsHealthy);
        Assert.Null(integrity.ExpectedSha256);
        Assert.Null(integrity.ChecksumMatches);
    }

    [Fact]
    public async Task Restore_ReplacesTheDatabaseAndKeepsARollbackSnapshot()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();
        await database.SeedRouteChainAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "snapshot.db"));

        await ExecuteAsync(database, "UPDATE Projects SET DisplayName = 'mutated' WHERE Id = 'project-1';");
        Assert.Equal("mutated", await ReadProjectDisplayNameAsync(database));

        var restored = await service.RestoreAsync(backup.BackupPath);

        Assert.NotNull(restored.RollbackPath);
        Assert.True(File.Exists(restored.RollbackPath));
        Assert.Equal(backup.Sha256, restored.Sha256);
        Assert.Equal("Test project", await ReadProjectDisplayNameAsync(database));

        var integrity = await service.VerifyIntegrityAsync();

        Assert.True(integrity.IsHealthy);
    }

    [Fact]
    public async Task Restore_ReturnedRollbackSnapshotCanBeRestoredThroughTheSameApi()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "snapshot.db"));
        await ExecuteAsync(database, "UPDATE Projects SET DisplayName = 'before restore' WHERE Id = 'project-1';");

        var restored = await service.RestoreAsync(backup.BackupPath);

        Assert.Equal("Test project", await ReadProjectDisplayNameAsync(database));
        Assert.NotNull(restored.RollbackPath);
        var rollbackIntegrity = await service.VerifyIntegrityAsync(restored.RollbackPath);
        Assert.True(rollbackIntegrity.IsHealthy);
        Assert.True(rollbackIntegrity.ChecksumMatches);

        await service.RestoreAsync(restored.RollbackPath);

        Assert.Equal("before restore", await ReadProjectDisplayNameAsync(database));
    }

    [Fact]
    public async Task Restore_RefusesABackupWithoutAChecksumSidecar()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "snapshot.db"));

        File.Delete(backup.ChecksumPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup.BackupPath));
    }

    [Fact]
    public async Task Restore_RejectsABrokenBackupWithoutTouchingTheLiveDatabase()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();
        await database.SeedRouteChainAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "snapshot.db"));

        var displayNameBefore = await ReadProjectDisplayNameAsync(database);

        await File.WriteAllBytesAsync(backup.BackupPath, new byte[64]);
        await File.WriteAllTextAsync(backup.ChecksumPath, ComputeFileHash(backup.BackupPath));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup.BackupPath));

        var integrity = await service.VerifyIntegrityAsync();

        Assert.True(integrity.IsHealthy);
        Assert.Equal(displayNameBefore, await ReadProjectDisplayNameAsync(database));
    }

    [Fact]
    public async Task Restore_RejectsAChecksumMismatchWithoutTouchingTheLiveDatabase()
    {
        using var database = new TestDatabase();

        await database.InitializeAsync();
        await database.SeedRouteChainAsync();

        var service = new DatabaseBackupService(database.Factory);
        var backup = await service.CreateBackupAsync(Path.Combine(database.Root, "snapshot.db"));

        var displayNameBefore = await ReadProjectDisplayNameAsync(database);

        await File.WriteAllTextAsync(backup.ChecksumPath, new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup.BackupPath));

        var integrity = await service.VerifyIntegrityAsync();

        Assert.True(integrity.IsHealthy);
        Assert.Equal(displayNameBefore, await ReadProjectDisplayNameAsync(database));
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadProjectDisplayNameAsync(TestDatabase database)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Projects WHERE Id = 'project-1';";

        return await command.ExecuteScalarAsync() as string;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
