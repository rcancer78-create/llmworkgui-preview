using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Data;

public sealed class ProviderHeadersMigrationTests
{
    [Fact]
    public async Task UpgradeFrom15_PreservesAllSecretPurposesAndOwners_AndOldProfilesHaveNoHeaders()
    {
        using var database = new TestDatabase();
        await new DatabaseMigrator(database.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 15).ToArray()).MigrateAsync();
        await using var connection = await database.Factory.OpenConnectionAsync();
        await Execute("""
            INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:provider','ProviderApiKey','Active','2026-10-04',NULL,NULL);
            INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:gateway','GatewayApiKey','Revoked','2026-10-04',NULL,'2026-10-04');
            INSERT INTO SecretReferenceOwners VALUES ('urn:llmworkgui:secret:provider','ProviderProfile','provider');
            INSERT INTO SecretReferenceOwners VALUES ('urn:llmworkgui:secret:gateway','Account','account');
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('provider','Provider','OpenCode','PrivateSource',1,'2026-10-04','2026-10-04');
            """);
        var report = await new DatabaseMigrator(database.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 16).ToArray()).MigrateAsync();
        Assert.Equal(16, Assert.Single(report.AppliedMigrations).Version);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM SecretReferences"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM SecretReferenceOwners"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM ProviderProfiles WHERE CustomHeadersJson IS NULL"));
        Assert.Null(await Scalar("PRAGMA foreign_key_check"));
        await Execute("INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:header','ProviderHeader','Active','2026-10-04',NULL,NULL);");
        await Execute("DELETE FROM SecretReferences WHERE Reference='urn:llmworkgui:secret:provider';");
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM SecretReferenceOwners"));
        Assert.True((await new DatabaseMigrator(database.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 16).ToArray()).MigrateAsync()).WasUpToDate);

        async Task Execute(string sql) { await using var cmd = connection.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var cmd = connection.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(); }
    }
}
