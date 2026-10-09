using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Data;

public sealed class HealthFailureHistoryMigrationTests
{
    [Fact]
    public async Task UpgradePreservesLegacyHealthWithoutInventingPerClassFailureHistory()
    {
        using var database = new TestDatabase();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(database.Factory, migrations.Where(m => m.Version <= 13).ToArray()).MigrateAsync();
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO HealthStates
                  (Id, ScopeType, ScopeId, State, ErrorClass, FailureCount, WindowStartedAtUtc,
                   CooldownUntilUtc, EvidenceRedactedJson, UpdatedAtUtc)
                VALUES ('account:legacy', 'account', 'legacy', 'Degraded', 'NetworkOrTimeout', 2,
                        '2026-10-04T00:00:00.0000000+00:00', NULL, NULL, '2026-10-04T00:14:00.0000000+00:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var report = await new DatabaseMigrator(database.Factory,
            migrations.Where(m => m.Version <= 14).ToArray()).MigrateAsync();

        Assert.Equal(14, report.CurrentSchemaVersion);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT State, FailureCount, FailureHistoryJson FROM HealthStates WHERE Id='account:legacy'";
        await using var rows = await read.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal("Degraded", rows.GetString(0));
        Assert.Equal(2, rows.GetInt32(1));
        Assert.True(rows.IsDBNull(2));
    }
}
