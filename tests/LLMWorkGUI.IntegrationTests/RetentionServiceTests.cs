using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Retention;
using LLMWorkGUI.Infrastructure.Retention;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class RetentionServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task RunAsync_DeletesExpiredDataAndPreservesActiveEntities()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-terminal", state: "Succeeded");
        await _database.SeedExecutionAsync("execution-active", state: "Running");

        var now = DateTimeOffset.UtcNow;

        await _database.InsertExecutionEventAsync(
            "event-old-terminal",
            "execution-terminal",
            0,
            now.AddDays(-40));
        await _database.InsertExecutionEventAsync(
            "event-new-terminal",
            "execution-terminal",
            1,
            now.AddDays(-1));
        await _database.InsertExecutionEventAsync(
            "event-old-active",
            "execution-active",
            0,
            now.AddDays(-40));

        await _database.InsertHealthEventAsync("health-old", now.AddDays(-200));
        await _database.InsertHealthEventAsync("health-new", now.AddDays(-10));

        await _database.InsertQuotaSnapshotAsync("quota-old", now.AddDays(-100));
        await _database.InsertQuotaSnapshotAsync("quota-new", now.AddDays(-10));

        await _database.InsertProjectLockAsync(
            "lock-1",
            _database.GetWorkspacePath(),
            "execution-terminal");

        var service = CreateService(now);

        var report = await service.RunAsync();

        Assert.Equal(1, DeletedRows(report, RetentionCategories.RawBackendEvents));
        Assert.Equal(1, DeletedRows(report, RetentionCategories.HealthAuditTransitions));
        Assert.Equal(1, DeletedRows(report, RetentionCategories.QuotaSnapshots));

        Assert.Equal(1, await _database.CountAsync("ExecutionEvents", "Id = 'event-new-terminal'"));
        Assert.Equal(1, await _database.CountAsync("ExecutionEvents", "Id = 'event-old-active'"));
        Assert.Equal(1, await _database.CountAsync("HealthEvents", "Id = 'health-new'"));
        Assert.Equal(1, await _database.CountAsync("QuotaSnapshots", "Id = 'quota-new'"));

        Assert.Equal(1, await _database.CountAsync("Projects"));
        Assert.Equal(1, await _database.CountAsync("Sessions"));
        Assert.Equal(2, await _database.CountAsync("Executions"));
        Assert.Equal(1, await _database.CountAsync("ProjectLocks", "ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task RunAsync_DownsamplesOldQuotaSnapshotsKeepingNewestPerDay()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        var now = DateTimeOffset.UtcNow;
        var day = new DateTimeOffset(now.AddDays(-40).UtcDateTime.Date, TimeSpan.Zero);

        await _database.InsertQuotaSnapshotAsync("quota-day-1", day.AddHours(1));
        await _database.InsertQuotaSnapshotAsync("quota-day-2", day.AddHours(2));
        await _database.InsertQuotaSnapshotAsync("quota-day-3", day.AddHours(3));
        await _database.InsertQuotaSnapshotAsync("quota-recent", now.AddDays(-10));

        var service = CreateService(now);

        var report = await service.RunAsync();

        Assert.Equal(0, DeletedRows(report, RetentionCategories.QuotaSnapshots));
        Assert.Equal(2, DeletedRows(report, RetentionCategories.QuotaSnapshotsDownsampled));
        Assert.Equal(1, await _database.CountAsync("QuotaSnapshots", "Id = 'quota-day-3'"));
        Assert.Equal(1, await _database.CountAsync("QuotaSnapshots", "Id = 'quota-recent'"));
        Assert.Equal(2, await _database.CountAsync("QuotaSnapshots"));
    }

    [Fact]
    public async Task RunAsync_DeletesExpiredLogsAndDiagnosticBundles()
    {
        await _database.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var logsDirectory = AppDataPaths.GetLogsDirectory(_database.Root);
        var diagnosticsDirectory = AppDataPaths.GetDiagnosticBundlesDirectory(_database.Root);

        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(diagnosticsDirectory);

        var oldLog = Path.Combine(logsDirectory, "server-old.log");
        var newLog = Path.Combine(logsDirectory, "server-new.log");
        var oldBundle = Path.Combine(diagnosticsDirectory, "bundle-old.zip");
        var newBundle = Path.Combine(diagnosticsDirectory, "bundle-new.zip");

        await File.WriteAllTextAsync(oldLog, "old log");
        await File.WriteAllTextAsync(newLog, "new log");
        await File.WriteAllTextAsync(oldBundle, "old bundle");
        await File.WriteAllTextAsync(newBundle, "new bundle");

        File.SetLastWriteTimeUtc(oldLog, now.AddDays(-20).UtcDateTime);
        File.SetLastWriteTimeUtc(newLog, now.AddDays(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(oldBundle, now.AddDays(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(newBundle, now.AddDays(-1).UtcDateTime);

        var service = CreateService(now);

        var report = await service.RunAsync();

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.False(File.Exists(oldBundle));
        Assert.True(File.Exists(newBundle));

        Assert.Equal(1, DeletedFiles(report, RetentionCategories.ProcessServerLogs));
        Assert.Equal(1, DeletedFiles(report, RetentionCategories.DiagnosticBundles));
    }

    [Fact]
    public async Task RunAsync_IsIdempotent()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-terminal", state: "Succeeded");
        await _database.InsertExecutionEventAsync(
            "event-old",
            "execution-terminal",
            0,
            DateTimeOffset.UtcNow.AddDays(-40));

        var service = CreateService(DateTimeOffset.UtcNow);

        var first = await service.RunAsync();
        var second = await service.RunAsync();

        Assert.True(first.TotalDatabaseRowsDeleted > 0);
        Assert.Equal(0, second.TotalDatabaseRowsDeleted);
        Assert.Equal(0, second.TotalFilesDeleted);
    }

    private RetentionService CreateService(DateTimeOffset now, RetentionOptions? options = null)
    {
        return new RetentionService(
            _database.Factory,
            Options.Create(options ?? new RetentionOptions()),
            new StorageOptions { AppDataDirectory = _database.Root },
            new FixedTimeProvider(now));
    }

    private static int DeletedRows(RetentionRunReport report, string category)
    {
        var result = report.Find(category);

        Assert.NotNull(result);

        return result!.DatabaseRowsDeleted;
    }

    private static int DeletedFiles(RetentionRunReport report, string category)
    {
        var result = report.Find(category);

        Assert.NotNull(result);

        return result!.FilesDeleted;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
