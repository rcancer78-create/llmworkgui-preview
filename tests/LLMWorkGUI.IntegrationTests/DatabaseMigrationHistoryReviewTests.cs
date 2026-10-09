using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class DatabaseMigrationHistoryReviewTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private SqliteConnectionFactory Factory => new(_directory.GetPath("history.db"));
    private static readonly DatabaseMigration First = new(10, "First", "CREATE TABLE FirstPayload (Id INTEGER);");
    private static readonly DatabaseMigration Second = new(20, "Second", "CREATE TABLE SecondPayload (Id INTEGER);");
    private static readonly DatabaseMigration Third = new(30, "Third", "CREATE TABLE ThirdPayload (Id INTEGER);");

    [Fact]
    public async Task MissingEarlierHistoryIsRejectedBeforeExecutingAnyPendingSql()
    {
        await new DatabaseMigrator(Factory, new[] { Second }).MigrateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseMigrator(Factory, new[] { First, Second, Third }).MigrateAsync());
        await AssertHistoryAndPendingTablesAsync(1);
    }

    [Fact]
    public async Task UnknownHistoryBelowCurrentVersionIsRejectedBeforeExecutingAnyPendingSql()
    {
        await new DatabaseMigrator(Factory, new[] { First }).MigrateAsync();
        await using (var connection = await Factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO _schema_migrations VALUES (0, 'Unknown', 'unknown', '2026-10-06T00:00:00Z');";
            await seed.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseMigrator(Factory, new[] { First, Second, Third }).MigrateAsync());
        await using var check = await Factory.OpenConnectionAsync();
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM _schema_migrations;";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('SecondPayload', 'ThirdPayload');";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OrderedPrefixWithSparseVersionsRemainsSupported()
    {
        await new DatabaseMigrator(Factory, new[] { First }).MigrateAsync();
        var report = await new DatabaseMigrator(Factory, new[] { Third, First, Second }).MigrateAsync();
        Assert.Equal(10, report.PreviousSchemaVersion);
        Assert.Equal(30, report.CurrentSchemaVersion);
        Assert.Equal(new[] { 20, 30 }, report.AppliedMigrations.Select(migration => migration.Version));
    }

    private async Task AssertHistoryAndPendingTablesAsync(long expectedHistory)
    {
        await using var connection = await Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM _schema_migrations;";
        Assert.Equal(expectedHistory, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('FirstPayload', 'ThirdPayload');";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        TestSqlitePool.Clear(Factory);
        _directory.Dispose();
    }
}
