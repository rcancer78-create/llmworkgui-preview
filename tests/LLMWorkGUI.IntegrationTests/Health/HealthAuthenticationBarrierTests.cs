using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthAuthenticationBarrierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedAccountIsBlockedEvenWhenRouteProjectionFailsOrCallerCancels(bool cancellation)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        using var cancelled = new CancellationTokenSource();
        if (cancellation)
        {
            health.TransitionRecorded += (_, change) =>
            {
                if (change.Scope == HealthScope.ForRoute("route-1")) cancelled.Cancel();
            };
        }
        else
        {
            await using var connection = await database.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER RejectRouteProjection BEFORE INSERT ON HealthEvents
                WHEN NEW.ScopeType = 'route'
                BEGIN SELECT RAISE(ABORT, 'injected route projection failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        try { await health.ReportFailureAsync(HealthScope.ForRoute("route-1"), HealthErrorClass.AuthenticationOrRefresh, cancellationToken: cancelled.Token); }
        catch (Exception ex) when (ex is SqliteException or OperationCanceledException or InvalidOperationException) { }
        var account = await health.GetSnapshotAsync(HealthScope.ForAccount("account-1"));
        Assert.False(account.IsRoutable);
        Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, account.ErrorClass);
        Assert.Single(await health.GetAuditAsync(HealthScope.ForAccount("account-1")));
    }
}
