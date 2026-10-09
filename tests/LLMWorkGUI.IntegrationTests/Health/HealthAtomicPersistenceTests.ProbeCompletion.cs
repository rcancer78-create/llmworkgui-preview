using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed partial class HealthAtomicPersistenceTests
{
    [Theory]
    [InlineData(HealthProbeCompletionKind.ModelSucceeded, HealthState.Healthy)]
    [InlineData(HealthProbeCompletionKind.Failed, HealthState.QuarantinedAuto)]
    public async Task ProbeCompletionAuditFailure_RetainsOwnershipForDurableRetry(
        HealthProbeCompletionKind kind, HealthState expectedState)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        var events = provider.GetRequiredService<IHealthEventRepository>();
        var scope = HealthScope.ForAccount("probe-persistence-retry");
        await health.ReportFailureAsync(scope, HealthErrorClass.AuthenticationOrRefresh);
        await health.ForceEnableAsync(scope, "test admission");
        var attempt = await health.TryBeginProbeAttemptAsync(scope, modelProbe: true, "test model probe");
        Assert.NotNull(attempt);
        var before = await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId);
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;

        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectProbeAudit BEFORE INSERT ON HealthEvents
                BEGIN SELECT RAISE(ABORT, 'injected probe audit failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() =>
            health.CompleteProbeAttemptAsync(attempt!, kind, "observed terminal result"));
        Assert.Equal(HealthState.Recovering, (await health.GetSnapshotAsync(scope)).State);
        Assert.Equal(before.Count, (await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId)).Count);
        Assert.Equal(0, notifications);
        Assert.Null(await health.TryBeginProbeAttemptAsync(scope, modelProbe: true, "must not duplicate provider work"));

        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectProbeAudit";
            await command.ExecuteNonQueryAsync();
        }
        var retry = await health.CompleteProbeAttemptAsync(attempt!, kind, "retry persistence only");
        Assert.True(retry.WasCurrent);
        Assert.Equal(expectedState, retry.Snapshot.State);
        Assert.Equal(kind == HealthProbeCompletionKind.ModelSucceeded, retry.VerifiedRecovery);
        Assert.Equal(before.Count + 1, (await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId)).Count);
        Assert.Equal(1, notifications);
        var late = await health.CompleteProbeAttemptAsync(attempt!, kind, "duplicate terminal observation");
        Assert.False(late.WasCurrent);
        Assert.False(late.VerifiedRecovery);
        Assert.Equal(expectedState, late.Snapshot.State);
    }
}
