using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Quotas;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Diagnostics;

/// <summary>
/// Composition-root evidence for the Phase 12 hardening services: the diagnostic bundle, the
/// database backup service and the crash recovery service must be reachable from the production
/// infrastructure graph, and the bundle must be exportable end-to-end there.
/// </summary>
public sealed class HardeningServiceRegistrationTests
{
    [Fact]
    public void AddHardeningServices_RegistersThePhase12Services()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);
        services.AddHardeningServices();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DiagnosticBundleService>(provider.GetRequiredService<IDiagnosticBundleService>());
        Assert.IsType<DatabaseBackupService>(provider.GetRequiredService<IDatabaseBackupService>());
        Assert.IsType<AppCrashRecoveryService>(provider.GetRequiredService<IAppCrashRecoveryService>());
    }

    [Fact]
    public void AddHardeningServices_RegistersTheMilestone12BStabilityServices()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);
        services.AddHardeningServices();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RetentionCleanupService>(provider.GetRequiredService<IRetentionCleanupService>());
        Assert.IsType<LongRunningExecutionService>(provider.GetRequiredService<ILongRunningExecutionService>());
        Assert.IsType<QuotaPollingSoakRunner>(provider.GetRequiredService<IQuotaPollingSoakRunner>());
        Assert.IsType<EndToEndWorkflowScenarioRunner>(
            provider.GetRequiredService<IEndToEndWorkflowScenarioRunner>());
    }

    [Fact]
    public async Task RegisteredDiagnosticBundleService_CanPreviewAndExportARedactedBundle()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);
        services.AddHardeningServices();

        using var provider = services.BuildServiceProvider();

        var migrator = provider.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync();

        var service = provider.GetRequiredService<IDiagnosticBundleService>();
        var preview = await service.PreviewAsync();

        Assert.False(preview.IsBlocked);
        Assert.Contains(preview.Files, file => file.RelativePath == "manifest.json");

        var result = await service.CreateBundleAsync(preview);

        Assert.True(File.Exists(result.BundlePath));

        TestSqlitePool.Clear(provider.GetRequiredService<ISqliteConnectionFactory>());
    }

    [Fact]
    public async Task RegisteredDatabaseBackupService_RoundTripsTheLiveDatabase()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);
        services.AddHardeningServices();

        using var provider = services.BuildServiceProvider();

        var migrator = provider.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync();

        var service = provider.GetRequiredService<IDatabaseBackupService>();
        var backup = await service.CreateBackupAsync();

        Assert.True(backup.IntegrityOk);
        Assert.True(File.Exists(backup.ChecksumPath));

        var integrity = await service.VerifyIntegrityAsync();

        Assert.True(integrity.IsHealthy);
        Assert.Null(integrity.ChecksumMatches);

        TestSqlitePool.Clear(provider.GetRequiredService<ISqliteConnectionFactory>());
    }
}
