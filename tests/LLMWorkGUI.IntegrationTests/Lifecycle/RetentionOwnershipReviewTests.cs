using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Retention;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Lifecycle;

public sealed class RetentionOwnershipReviewTests
{
    [Fact]
    public async Task ReducedCompositionCanDisplayDiagnosticsButCannotCleanWithoutSupervisor()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("must-remain-absent");
        var services = new ServiceCollection();
        services.AddSingleton(new StorageOptions { AppDataDirectory = root });
        services.AddHardeningServices();
        using var provider = services.BuildServiceProvider();
        var cleanup = provider.GetRequiredService<IRetentionCleanupService>();
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => cleanup.CleanupAsync());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SecondaryHostCannotRunEitherDestructiveRetentionService()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        using var primary = HostBootstrapper.BuildHost(appDataDirectory: db.Root);
        Assert.True(primary.Services.GetRequiredService<IApplicationInstanceGuard>().IsPrimarySupervisor);
        var diagnostics = Path.Combine(db.Root, "diagnostics");
        Directory.CreateDirectory(diagnostics);
        var canary = Path.Combine(diagnostics, "bundle-stale.zip");
        await File.WriteAllTextAsync(canary, "retained");
        File.SetLastWriteTimeUtc(canary, DateTime.UtcNow.AddYears(-2));
        using var secondary = HostBootstrapper.BuildHost(appDataDirectory: db.Root);
        Assert.True(secondary.Services.GetRequiredService<IApplicationInstanceGuard>().IsViewOnly);
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() =>
            secondary.Services.GetRequiredService<IRetentionCleanupService>().CleanupAsync());
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() =>
            secondary.Services.GetRequiredService<IRetentionService>().RunAsync());
        var backup = secondary.Services.GetRequiredService<IDatabaseBackupService>();
        var destination = Path.Combine(db.Root, "forbidden-backup", "copy.db");
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => backup.CreateBackupAsync(destination));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => backup.RestoreAsync(destination));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.True((await backup.VerifyIntegrityAsync()).IsHealthy);
        Assert.Equal("retained", await File.ReadAllTextAsync(canary));
    }
}
