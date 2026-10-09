using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Retention;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class HostBootstrapperTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        TestSqlitePool.Clear(new SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        _directory.Dispose();
    }

    [Fact]
    public async Task BuildHost_ResolvesServicesAndMigratesDatabase()
    {
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);

        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);

        Assert.NotNull(host.Services.GetRequiredService<ISqliteConnectionFactory>());
        Assert.NotNull(host.Services.GetRequiredService<DatabaseMigrator>());
        Assert.NotNull(host.Services.GetRequiredService<IProjectRepository>());
        Assert.NotNull(host.Services.GetRequiredService<ISessionRepository>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionRepository>());
        Assert.NotNull(host.Services.GetRequiredService<IProjectLockRepository>());
        Assert.NotNull(host.Services.GetRequiredService<IHealthStateRepository>());
        Assert.NotNull(host.Services.GetRequiredService<IApplicationSettingsRepository>());
        Assert.NotNull(host.Services.GetRequiredService<IRetentionService>());
        Assert.NotNull(host.Services.GetRequiredService<IActivityTimelineService>());
        Assert.NotNull(host.Services.GetRequiredService<SensitiveDataFilter>());
        Assert.NotNull(host.Services.GetRequiredService<WorkflowBlobStore>());
        Assert.NotNull(host.Services.GetRequiredService<ISecretStore>());
        Assert.NotNull(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("host-test"));

        var retention = host.Services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        Assert.Equal(30, retention.RawBackendEventsRetentionDays);
        Assert.Equal(14, retention.ProcessServerLogsRetentionDays);
        Assert.Equal(7, retention.DiagnosticBundlesRetentionDays);
        Assert.Equal(90, retention.QuotaSnapshotsRetentionDays);
        Assert.Equal(30, retention.QuotaSnapshotsDownsampleDays);
        Assert.Equal(180, retention.HealthAuditTransitionsRetentionDays);

        var storage = host.Services.GetRequiredService<StorageOptions>();

        Assert.Equal(Path.GetFullPath(_directory.Root), storage.AppDataDirectory);
        Assert.Equal(StorageOptions.DefaultDatabaseFileName, storage.DatabaseFileName);
        Assert.True(File.Exists(Path.Combine(_directory.Root, StorageOptions.DefaultDatabaseFileName)));

        await host.StopAsync();
    }

    [Fact]
    public async Task BuildHost_BindsConfigurationOverrides()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Retention:RawBackendEventsRetentionDays"] = "45",
            ["Storage:DatabaseFileName"] = "custom.db"
        };

        using var host = HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _directory.Root)
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings))
            .Build();

        await HostBootstrapper.InitializeAsync(host);

        var retention = host.Services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        Assert.Equal(45, retention.RawBackendEventsRetentionDays);
        Assert.Equal(30, retention.QuotaSnapshotsDownsampleDays);

        var storage = host.Services.GetRequiredService<StorageOptions>();

        Assert.Equal("custom.db", storage.DatabaseFileName);
        Assert.True(File.Exists(Path.Combine(_directory.Root, "custom.db")));
    }
}
