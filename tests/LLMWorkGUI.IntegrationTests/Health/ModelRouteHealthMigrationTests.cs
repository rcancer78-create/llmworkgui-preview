using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class ModelRouteHealthMigrationTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private SqliteConnectionFactory Factory => new(_directory.GetPath("health.db"));
    public void Dispose() { TestSqlitePool.Clear(Factory); _directory.Dispose(); }

    [Fact]
    public async Task UpgradeProjectsEveryAmbiguousPairWithoutClearingNegativeEvidenceOrHistory()
    {
        var factory = Factory;
        await new DatabaseMigrator(factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 18).ToArray()).MigrateAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO ProviderProfiles(Id,DisplayName,Backend,MaxDataClass,CreatedAtUtc,UpdatedAtUtc)
                VALUES('p','P','OpenCode','PrivateSource','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                INSERT INTO Accounts(Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('a','p','A','Valid','Healthy','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
                       ('a:b','p','AB','Valid','Healthy','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
                       ('акк:🧪','p','Unicode','Valid','Healthy','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
                       ('only','p','Only','Valid','Healthy','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                INSERT INTO Models(Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Health,Provenance,DiscoveredAtUtc)
                VALUES('m','OpenCode','p','native','M','Supported','Healthy','Native','2026-10-01T00:00:00Z');
                INSERT INTO Routes(Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES('only:model','OpenCode','p','only','m','PrivateSource','Healthy','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,FailureCount,UpdatedAtUtc,FailureHistoryJson)
                VALUES ('legacy-block','route','a:b:c','DisabledManual',0,'2026-10-01T00:00:00Z','[]'),
                       ('legacy-positive','route','a:b:ok','Healthy',0,'2026-10-01T00:00:00Z',NULL),
                       ('legacy-unicode','route','акк:🧪:модель:α','QuarantinedAuto',0,'2026-10-01T00:00:00Z',NULL),
                       ('legacy-opaque','route','only:model','ForcedEnabled',0,'2026-10-01T00:00:00Z',NULL),
                       ('legacy-unique','route','a:single','Healthy',0,'2026-10-01T00:00:00Z',NULL),
                       ('legacy-no-account','route','mirasim:remote:model','DisabledManual',0,'2026-10-01T00:00:00Z',NULL);
                INSERT INTO HealthEvents(Id,ScopeType,ScopeId,NewState,Reason,OccurredAtUtc)
                VALUES('original-event','route','a:b:c','DisabledManual','Original user decision','2026-10-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        await new DatabaseMigrator(factory).MigrateAsync();
        Assert.True((await new DatabaseMigrator(factory).MigrateAsync()).WasUpToDate);
        var states = new SqliteHealthStateRepository(factory);
        foreach (var scope in new[] { HealthScope.ForModelRoute("a", "b:c"), HealthScope.ForModelRoute("a:b", "c") })
        {
            var row = await states.GetAsync(scope.ScopeType, scope.ScopeId);
            Assert.NotNull(row);
            Assert.Equal(HealthState.DisabledManual, row.State);
            Assert.NotNull(row.FailureHistory);
            Assert.Empty(row.FailureHistory);
        }
        foreach (var scope in new[] { HealthScope.ForModelRoute("a", "b:ok"), HealthScope.ForModelRoute("a:b", "ok") })
            Assert.Equal(HealthState.ProbeRequired, (await states.GetAsync(scope.ScopeType, scope.ScopeId))!.State);
        var unicode = HealthScope.ForModelRoute("акк:🧪", "модель:α");
        Assert.Equal(HealthState.QuarantinedAuto, (await states.GetAsync(unicode.ScopeType, unicode.ScopeId))!.State);
        Assert.Equal(HealthState.DisabledManual, (await states.GetAsync("route", "a:b:c"))!.State);
        var opaqueCollision = HealthScope.ForModelRoute("only", "model");
        Assert.Equal(HealthState.ProbeRequired, (await states.GetAsync(opaqueCollision.ScopeType, opaqueCollision.ScopeId))!.State);
        Assert.Equal(HealthState.ProbeRequired, (await states.GetAsync("route", "only:model"))!.State);
        var unique = HealthScope.ForModelRoute("a", "single");
        Assert.Equal(HealthState.Healthy, (await states.GetAsync(unique.ScopeType, unique.ScopeId))!.State);
        var noAccount = HealthScope.ForModelRoute("mirasim:remote", "model");
        Assert.Equal(HealthState.DisabledManual, (await states.GetAsync(noAccount.ScopeType, noAccount.ScopeId))!.State);
        await using var verified = await factory.OpenConnectionAsync();
        await using var audit = verified.CreateCommand();
        audit.CommandText = "SELECT Reason FROM HealthEvents WHERE Id='original-event';";
        Assert.Equal("Original user decision", await audit.ExecuteScalarAsync());
        audit.CommandText = "SELECT count(*) FROM HealthEvents WHERE ScopeType='model-route';";
        Assert.Equal(11L, await audit.ExecuteScalarAsync());
    }
}
