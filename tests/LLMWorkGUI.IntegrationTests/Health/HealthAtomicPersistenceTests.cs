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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditInsertFailure_RollsBackSnapshotAndDoesNotPublish(bool existingState)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        var states = provider.GetRequiredService<IHealthStateRepository>();
        var events = provider.GetRequiredService<IHealthEventRepository>();
        var scope = HealthScope.ForAccount("atomic-health-account");
        if (existingState) await health.ReportSuccessAsync(scope);
        var before = await states.GetAsync(scope.ScopeType, scope.ScopeId);
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;

        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectHealthAudit BEFORE INSERT ON HealthEvents
                BEGIN SELECT RAISE(ABORT, 'injected health audit failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => health.ReportFailureAsync(scope,
            HealthErrorClass.AuthenticationOrRefresh));

        Assert.Equal(before, await states.GetAsync(scope.ScopeType, scope.ScopeId));
        Assert.Empty(await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId));
        Assert.Equal(0, notifications);
        Assert.True((await health.GetSnapshotAsync(scope)).AuthenticationFanoutPending);
        Assert.False((await health.GetSnapshotAsync(scope)).IsRoutable);
    }

    [Fact]
    public async Task PersistedRouteAuthenticationFailure_BlocksItsAuthoritativelyResolvedAccount()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();

        await health.ReportFailureAsync(HealthScope.ForRoute("route-1"), HealthErrorClass.AuthenticationOrRefresh);

        var account = await health.GetSnapshotAsync(HealthScope.ForAccount("account-1"));
        Assert.False(account.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, account.ErrorClass);
    }

    [Fact]
    public async Task UnknownCompositeRoute_DoesNotInventAnAccountAssignment()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();

        await health.ReportFailureAsync(HealthScope.ForRoute("account-1:unconfigured-model"),
            HealthErrorClass.AuthenticationOrRefresh);

        Assert.True((await health.GetSnapshotAsync(HealthScope.ForAccount("account-1"))).IsRoutable);
        Assert.Empty(await health.GetAuditAsync(HealthScope.ForAccount("account-1")));
    }
}
