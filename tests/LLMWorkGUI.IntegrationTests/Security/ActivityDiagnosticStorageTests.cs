using System.Security.Cryptography;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

[Collection("Activity storage isolation")]
public sealed class ActivityDiagnosticStorageTests : IDisposable
{
    private const string Secret = "123456789";
    private const string Payload = "{\"password\":123456789,\"remaining\":7}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmworkgui-activity-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public ActivityDiagnosticStorageTests() => _factory = new SqliteConnectionFactory(Path.Combine(_root, "llmworkgui.db"));

    [Fact]
    public async Task StartupRemovesLegacyFieldsAndSearchMatchesBeforeRestoringHistory()
    {
        await SeedAsync();
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _root);
        await HostBootstrapper.InitializeAsync(host);
        await AssertCleanAsync(_factory.DatabasePath);
    }

    [Fact]
    public async Task BackupCleansItsOwnRowsAndIndexWithoutChangingSource()
    {
        await SeedAsync();
        var backup = await new DatabaseBackupService(_factory).CreateBackupAsync(Path.Combine(_root, "backup.db"));
        await AssertCleanAsync(backup.BackupPath);
        Assert.Equal(Payload, await ScalarAsync(_factory.DatabasePath, "SELECT DescriptionRedacted FROM ActivityEvents WHERE Id='legacy';"));
        Assert.True((await new DatabaseBackupService(_factory).VerifyIntegrityAsync(backup.BackupPath)).ChecksumMatches);
    }

    [Fact]
    public async Task RestoreCleansStagedLegacyBackupAndPreservesOwnersOriginalBytes()
    {
        await SeedAsync();
        var path = Path.Combine(_root, "owner-backup.db");
        await using (var connection = await _factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync();
        }
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)));
        using (var pooled = _factory.CreateConnection()) { SqliteConnection.ClearPool(pooled); }
        var restored = await new DatabaseBackupService(_factory).RestoreAsync(path);
        await AssertCleanAsync(_factory.DatabasePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(restored.Sha256, (await new DatabaseBackupService(_factory).VerifyIntegrityAsync()).Sha256);
    }

    private async Task SeedAsync()
    {
        await new DatabaseMigrator(_factory).MigrateAsync();
        // Deliberately bypass current Activity Center ingestion to represent a retained legacy row.
        await new SqliteActivityEventJournal(_factory).AppendAsync(new ActivityEvent(
            "legacy", DateTimeOffset.UtcNow, ActivityEventKind.System, ActivityRoleNames.System,
            ActivityEventState.Warning, ActivityEventSource.Synthetic, Payload, Payload,
            sessionId: "preserved-session", routeId: "preserved-route", artifactName: "preserved.json"));
        Assert.Equal(1L, await ScalarAsync(_factory.DatabasePath,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH '123456789';"));
    }

    [Fact]
    public async Task MaintenanceHandlesMultiplePagesNegativeRowidsAndPreservesCleanBytesAndProjection()
    {
        await SeedAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        var original = Assert.Single(await journal.LoadNewestAsync(1));
        for (var index = 0; index < 205; index++)
        {
            await journal.AppendAsync(new ActivityEvent("page-" + index, original.OccurredAtUtc,
                original.Kind, original.Role, original.State, original.Source, Payload, Payload));
        }
        const string clean = "  { \"remaining\" : 7 }  ";
        await journal.AppendAsync(new ActivityEvent("clean", original.OccurredAtUtc, original.Kind,
            original.Role, original.State, original.Source, clean, clean));
        await using var connection = await _factory.OpenConnectionAsync();
        await using (var move = connection.CreateCommand())
        {
            move.CommandText = "UPDATE ActivityEventsSearch SET rowid=-17 WHERE rowid=(SELECT rowid FROM ActivityEvents WHERE Id='legacy'); UPDATE ActivityEvents SET rowid=-17 WHERE Id='legacy';";
            await move.ExecuteNonQueryAsync();
        }
        Assert.Equal(206, await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, CancellationToken.None));
        Assert.Equal(0, await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, CancellationToken.None));
        Assert.Equal(clean, await ScalarAsync(_factory.DatabasePath, "SELECT DescriptionRedacted FROM ActivityEvents WHERE Id='clean';"));
        Assert.Equal(-17L, await ScalarAsync(_factory.DatabasePath, "SELECT rowid FROM ActivityEvents WHERE Id='legacy';"));
        var events = await journal.LoadNewestAsync(300);
        Assert.Equal(207, events.Count);
        foreach (var activityEvent in events)
        {
            await using var body = connection.CreateCommand();
            body.CommandText = "SELECT body FROM ActivityEventsSearch WHERE rowid=(SELECT rowid FROM ActivityEvents WHERE Id=$id);";
            body.Parameters.AddWithValue("$id", activityEvent.Id);
            Assert.Equal(ActivityEventSearchText.Build(activityEvent), await body.ExecuteScalarAsync());
        }
        await AssertCleanAsync(_factory.DatabasePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRepairsStaleOrMissingIndexEvenWhenFieldsAlreadyClean(bool missing)
    {
        await SeedAsync();
        await using var connection = await _factory.OpenConnectionAsync();
        await using (var cleanFields = connection.CreateCommand())
        {
            cleanFields.CommandText = "UPDATE ActivityEvents SET TitleRedacted='clean title', DescriptionRedacted='clean description';"
                + (missing ? "DELETE FROM ActivityEventsSearch;" : string.Empty);
            await cleanFields.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, CancellationToken.None));
        Assert.Equal(0L, await ScalarAsync(_factory.DatabasePath,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH '123456789';"));
        Assert.Equal(1L, await ScalarAsync(_factory.DatabasePath,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH 'description';"));
    }

    [Fact]
    public async Task FailedPageRollsBackBothFieldsAndSearchIndex()
    {
        await SeedAsync();
        await new SqliteActivityEventJournal(_factory).AppendAsync(new ActivityEvent("second", DateTimeOffset.UtcNow,
            ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Warning, ActivityEventSource.Synthetic,
            Payload, Payload));
        await using var connection = await _factory.OpenConnectionAsync();
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_maintenance BEFORE UPDATE ON ActivityEvents WHEN OLD.Id='second' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, CancellationToken.None));
        Assert.Equal(Payload, await ScalarAsync(_factory.DatabasePath, "SELECT TitleRedacted FROM ActivityEvents WHERE Id='legacy';"));
        Assert.Equal(2L, await ScalarAsync(_factory.DatabasePath,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH '123456789';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringPageDoesNotPublishPartialRepair(bool duringPage)
    {
        await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        await using var connection = await _factory.OpenConnectionAsync();
        if (duringPage)
        {
            connection.CreateFunction("cancel_maintenance", () => { cancellation.Cancel(); return 0; });
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER cancel_page BEFORE UPDATE ON ActivityEvents BEGIN SELECT cancel_maintenance(); END;";
            await trigger.ExecuteNonQueryAsync();
        }
        else { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, cancellation.Token));
        Assert.Equal(Payload, await ScalarAsync(_factory.DatabasePath, "SELECT TitleRedacted FROM ActivityEvents WHERE Id='legacy';"));
        Assert.Equal(1L, await ScalarAsync(_factory.DatabasePath,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH '123456789';"));
    }

    [Fact]
    public async Task MaintenanceSupportsOlderDatabaseWithoutSearchTableAndNoActivitySchema()
    {
        await using var empty = await _factory.OpenConnectionAsync();
        Assert.Equal(0, await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(empty, CancellationToken.None));
        await SeedAsync();
        await using (var drop = empty.CreateCommand())
        {
            drop.CommandText = "DROP TABLE ActivityEventsSearch;";
            await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(empty, CancellationToken.None));
        var value = Assert.IsType<string>(await ScalarAsync(_factory.DatabasePath,
            "SELECT DescriptionRedacted FROM ActivityEvents WHERE Id='legacy';"));
        Assert.DoesNotContain(Secret, value, StringComparison.Ordinal);
    }

    private static async Task AssertCleanAsync(string path)
    {
        foreach (var column in new[] { "TitleRedacted", "DescriptionRedacted" })
        {
            var value = Assert.IsType<string>(await ScalarAsync(path, $"SELECT {column} FROM ActivityEvents WHERE Id='legacy';"));
            Assert.DoesNotContain(Secret, value, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", value, StringComparison.Ordinal);
            Assert.Contains("\"remaining\":7", value, StringComparison.Ordinal);
        }
        Assert.Equal(0L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH '123456789';"));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH 'preserved';"));
        Assert.Equal("preserved-session|preserved-route|preserved.json", await ScalarAsync(path,
            "SELECT SessionId||'|'||RouteId||'|'||ArtifactName FROM ActivityEvents WHERE Id='legacy';"));
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        using (var connection = _factory.CreateConnection()) { SqliteConnection.ClearPool(connection); }
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "llmworkgui-activity-storage-tests"));
        var target = Path.GetFullPath(_root);
        if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Unexpected fixture directory."); }
        // Other integration fixtures reset all SQLite pools. Run this class in isolation and
        // drain delayed native-handle finalizers before deleting our own verified temp directory.
        for (var attempt = 0; ; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
                break;
            }
            catch (IOException) when (attempt < 9) { Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }
}

[CollectionDefinition("Activity storage isolation", DisableParallelization = true)]
public sealed class ActivityStorageIsolationCollection;
