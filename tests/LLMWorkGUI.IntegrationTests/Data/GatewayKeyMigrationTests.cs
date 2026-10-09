using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Data;

public sealed class GatewayKeyMigrationTests
{
    [Fact]
    public async Task UpgradePreservesReferencesOwnersAndForeignKeyCascadeAndAllowsDedicatedPurpose()
    {
        using var database = new TestDatabase();
        await new DatabaseMigrator(database.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 12).ToArray()).MigrateAsync();
        await using var connection = await database.Factory.OpenConnectionAsync();
        await Execute("""
            INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:fixture','ProviderApiKey','Active','2026-10-03',NULL,NULL);
            INSERT INTO SecretReferenceOwners VALUES ('urn:llmworkgui:secret:fixture','ProviderProfile','provider');
            INSERT INTO SecretReferenceOwners VALUES ('urn:llmworkgui:secret:fixture','Account','account');
            """);
        var targetMigrations = DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 13).ToArray();
        var report = await new DatabaseMigrator(database.Factory, targetMigrations).MigrateAsync();
        Assert.Equal(12, report.PreviousSchemaVersion);
        Assert.Equal(13, report.CurrentSchemaVersion);
        Assert.Equal(13, Assert.Single(report.AppliedMigrations).Version);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM SecretReferences WHERE Kind='ProviderApiKey' AND State='Active'"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM SecretReferenceOwners"));
        Assert.Null(await Scalar("PRAGMA foreign_key_check"));
        await Execute("INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:gateway','GatewayApiKey','Active','2026-10-03',NULL,NULL)");
        await Assert.ThrowsAsync<SqliteException>(() => Execute("INSERT INTO SecretReferenceOwners VALUES ('urn:llmworkgui:secret:missing','Account','missing')"));
        await Execute("DELETE FROM SecretReferences WHERE Reference='urn:llmworkgui:secret:fixture'");
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM SecretReferenceOwners"));
        Assert.True((await new DatabaseMigrator(database.Factory, targetMigrations).MigrateAsync()).WasUpToDate);

        async Task Execute(string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    }
}
