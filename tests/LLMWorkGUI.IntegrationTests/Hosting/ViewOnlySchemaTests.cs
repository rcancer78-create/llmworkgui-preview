using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Hosting;

public sealed class ViewOnlySchemaTests
{
    [Fact]
    public async Task SecondaryStartupRefusesAnOldSchemaWithoutApplyingMigrations()
    {
        using var database = new TestDatabase();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(database.Factory, migrations.Where(m => m.Version <= 13).ToArray()).MigrateAsync();
        using var host = SecondaryHost(database.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => HostBootstrapper.InitializeAsync(host));

        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM _schema_migrations";
        Assert.Equal(13L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('HealthStates') WHERE name='FailureHistoryJson'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SecondaryStartupAcceptsTheCurrentVerifiedSchema()
    {
        using var database = new TestDatabase();
        await new DatabaseMigrator(database.Factory).MigrateAsync();
        using var host = SecondaryHost(database.Factory);
        await HostBootstrapper.InitializeAsync(host);
    }

    [Fact]
    public async Task ReadOnlyValidationDoesNotCreateAMissingDatabase()
    {
        using var database = new TestDatabase();
        await Assert.ThrowsAsync<SqliteException>(() =>
            new DatabaseMigrator(database.Factory).ValidateCurrentSchemaAsync());
        Assert.False(File.Exists(database.Factory.DatabasePath));
    }

    private static IHost SecondaryHost(ISqliteConnectionFactory factory) => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton(factory);
            services.AddSingleton<DatabaseMigrator>();
            services.AddSingleton<IApplicationInstanceGuard>(new ViewOnlyGuard());
        }).Build();

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "synthetic-secondary";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View-only fixture");
        public void Dispose() { }
    }
}
