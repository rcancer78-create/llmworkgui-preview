using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Infrastructure.Lifecycle;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Lifecycle;

/// <summary>
/// Phase 12 Milestone 12B: retention cleanup deletes only stale diagnostic bundles and abandoned
/// scratch workspaces, archives old execution events of terminal executions before deleting them and
/// never touches active rows or immutable workflow versions.
/// </summary>
public sealed class RetentionCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StaleTimestamp = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecentTimestamp = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Cleanup_DeletesOnlyStaleBundlesAndScratchWorkspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "llmworkgui-retention-" + Guid.NewGuid().ToString("N"));

        try
        {
            var diagnostics = Path.Combine(root, "diagnostics");
            var staleBundle = Path.Combine(diagnostics, "bundle-stale.zip");
            var recentBundle = Path.Combine(diagnostics, "bundle-recent.zip");
            var staleSidecar = Path.ChangeExtension(staleBundle, ".sha256");

            Directory.CreateDirectory(diagnostics);
            await File.WriteAllTextAsync(staleBundle, "stale");
            await File.WriteAllTextAsync(staleSidecar, "hash");
            await File.WriteAllTextAsync(recentBundle, "recent");

            File.SetLastWriteTimeUtc(staleBundle, StaleTimestamp.UtcDateTime);
            File.SetLastWriteTimeUtc(staleSidecar, StaleTimestamp.UtcDateTime);
            File.SetLastWriteTimeUtc(recentBundle, RecentTimestamp.UtcDateTime);

            var scratch = Path.Combine(root, "scratch", "imports");
            var staleWorkspace = Path.Combine(scratch, "scope-stale");
            var recentWorkspace = Path.Combine(scratch, "scope-recent");

            Directory.CreateDirectory(staleWorkspace);
            Directory.CreateDirectory(recentWorkspace);
            await File.WriteAllTextAsync(Path.Combine(staleWorkspace, "workflow.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(recentWorkspace, "workflow.json"), "{}");

            SetTreeLastWriteTimeUtc(staleWorkspace, StaleTimestamp.UtcDateTime);
            SetTreeLastWriteTimeUtc(recentWorkspace, RecentTimestamp.UtcDateTime);

            var service = CreateService(root);
            var report = await service.CleanupAsync();

            Assert.False(File.Exists(staleBundle));
            Assert.False(File.Exists(staleSidecar));
            Assert.True(File.Exists(recentBundle));
            Assert.False(Directory.Exists(staleWorkspace));
            Assert.True(Directory.Exists(recentWorkspace));

            Assert.Equal(1, report.DeletedDiagnosticBundleCount);
            Assert.Equal(1, report.DeletedScratchWorkspaceCount);
            Assert.Equal(2, report.PreservedActiveItemCount);
            Assert.Equal(0, report.SkippedItemCount);
            Assert.Contains(staleBundle, report.DeletedPaths);
            Assert.Contains(staleWorkspace, report.DeletedPaths);
            Assert.DoesNotContain(recentBundle, report.DeletedPaths);
            Assert.Empty(report.Warnings);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Cleanup_ArchivesStaleTerminalExecutionEventsAndPreservesActiveOnes()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync();
        await _host.SeedExecutionAsync(
            "execution-terminal",
            state: "Completed",
            endedAt: StaleTimestamp);
        await _host.SeedExecutionAsync("execution-active", state: "Running");
        await _host.SeedWorkflowAsync();

        await InsertExecutionEventAsync("event-old-terminal", "execution-terminal", 0, StaleTimestamp);
        await InsertExecutionEventAsync("event-old-active", "execution-active", 0, StaleTimestamp);
        await InsertExecutionEventAsync("event-recent-terminal", "execution-terminal", 1, RecentTimestamp);

        var service = CreateService(_host.Root, _host.Factory);
        var report = await service.CleanupAsync();

        Assert.Equal(1, report.ArchivedAuditRecordCount);
        Assert.Equal(1, report.DeletedAuditRecordCount);

        Assert.Equal(1, await _host.CountAsync("ExecutionEvents", "ExecutionId = 'execution-terminal'"));
        Assert.Equal(1, await _host.CountAsync("ExecutionEvents", "ExecutionId = 'execution-active'"));

        var archiveDirectory = Path.Combine(_host.Root, "diagnostics", "archive");
        var archiveFile = Assert.Single(Directory.GetFiles(archiveDirectory, "*.jsonl"));
        var archiveContent = await File.ReadAllTextAsync(archiveFile);

        Assert.Contains("event-old-terminal", archiveContent, StringComparison.Ordinal);
        Assert.DoesNotContain("event-old-active", archiveContent, StringComparison.Ordinal);
        Assert.DoesNotContain("event-recent-terminal", archiveContent, StringComparison.Ordinal);

        // Immutable workflow versions and active runs are never touched by the cleanup.
        Assert.Equal(1, await _host.CountAsync("WorkflowPackages"));
        Assert.Equal(1, await _host.CountAsync("WorkflowVersions"));
        Assert.Equal(1, await _host.CountAsync("WorkflowRuns"));
        Assert.Equal(1, await _host.CountAsync("WorkflowRuns", "State = 'Running'"));
    }

    [Fact]
    public async Task Cleanup_DoesNotArchiveHealthAuditEvents()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.InsertHealthEventAsync("health-old", "account", "account-1", occurredAt: StaleTimestamp);

        var service = CreateService(_host.Root, _host.Factory);
        var report = await service.CleanupAsync();

        Assert.Equal(0, report.ArchivedAuditRecordCount);
        Assert.Equal(1, await _host.CountAsync("HealthEvents"));
    }

    [Fact]
    public async Task Cleanup_PreservesUnresolvedAmbiguousExecutionEvidence()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync();
        await _host.SeedExecutionAsync("uncertain", state: "Ambiguous");
        await InsertExecutionEventAsync("old-uncertain", "uncertain", 0, StaleTimestamp);

        var report = await CreateService(_host.Root, _host.Factory).CleanupAsync();

        Assert.Equal(1, await _host.CountAsync("ExecutionEvents", "ExecutionId = 'uncertain'"));
        Assert.Equal(0, report.ArchivedAuditRecordCount);
        Assert.Equal(0, report.DeletedAuditRecordCount);
    }

    private static RetentionCleanupService CreateService(
        string root,
        LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory? factory = null) =>
        new(
            new StorageOptions { AppDataDirectory = root },
            new FixedTimeProvider(Now),
            RetentionPolicy.Default,
            factory);

    private Task InsertExecutionEventAsync(
        string id,
        string executionId,
        int sequence,
        DateTimeOffset occurredAt)
    {
        return _host.ExecuteAsync(
            """
            INSERT INTO ExecutionEvents
                (Id, ExecutionId, Sequence, EventKind, NormalizedRedactedPayloadJson, OccurredAtUtc)
            VALUES ($id, $executionId, $sequence, 'BackendEvent', '{}', $occurredAtUtc);
            """,
            ("$id", id),
            ("$executionId", executionId),
            ("$sequence", sequence),
            ("$occurredAtUtc", occurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void SetTreeLastWriteTimeUtc(string directory, DateTime timestampUtc)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, timestampUtc);
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(subDirectory, timestampUtc);
        }

        Directory.SetLastWriteTimeUtc(directory, timestampUtc);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
