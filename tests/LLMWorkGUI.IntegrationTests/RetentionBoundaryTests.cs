using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Retention;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class RetentionBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-retention-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("scratch")]
    [InlineData("scratch/imports")]
    [InlineData("scratch/imports/workspace")]
    [InlineData("scratch/imports/workspace/nested")]
    [InlineData("data-root")]
    [InlineData("data-ancestor")]
    public async Task Cleanup_PreservesScratchBehindJunction(string position)
    {
        var data = Path.Combine(_root, "data");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string targetWorkspace;
        if (position == "data-root")
        {
            CreateJunction(data, outside);
            targetWorkspace = Path.Combine(outside, "scratch", "imports", "workspace");
        }
        else if (position == "data-ancestor")
        {
            var ancestor = Path.Combine(_root, "redirect");
            CreateJunction(ancestor, outside);
            data = Path.Combine(ancestor, "data");
            targetWorkspace = Path.Combine(outside, "data", "scratch", "imports", "workspace");
        }
        else
        {
            var link = Path.Combine(data, position.Replace('/', Path.DirectorySeparatorChar));
            CreateJunction(link, outside);
            targetWorkspace = position switch
            {
                "scratch" => Path.Combine(outside, "imports", "workspace"),
                "scratch/imports" => Path.Combine(outside, "workspace"),
                _ => outside
            };
        }

        var canary = WriteOldFile(Path.Combine(targetWorkspace, "canary.txt"));
        AgeDirectories(_root);
        var report = await new RetentionCleanupService(new StorageOptions { AppDataDirectory = data }).CleanupAsync();

        Assert.True(File.Exists(canary), "Cleanup deleted a file outside the managed scratch tree.");
        Assert.Equal("synthetic retention canary", File.ReadAllText(canary));
        Assert.Equal(0, report.DeletedScratchWorkspaceCount);
        Assert.True(report.SkippedItemCount > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_PreservesBundlesBehindRootOrAncestorJunction(bool ancestor)
    {
        var data = Path.Combine(_root, "data");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        CreateJunction(ancestor ? data : Path.Combine(data, "diagnostics"), outside);
        var canary = WriteOldFile(Path.Combine(outside, ancestor ? "diagnostics" : "", "bundle.zip"));
        var report = await new RetentionCleanupService(new StorageOptions { AppDataDirectory = data }).CleanupAsync();
        Assert.True(File.Exists(canary));
        Assert.Equal(0, report.DeletedDiagnosticBundleCount);
        Assert.True(report.SkippedItemCount > 0);
    }

    [Fact]
    public async Task Cleanup_RecentHiddenFilePreservesWorkspaceButOrdinaryStaleWorkspaceIsDeleted()
    {
        var data = Path.Combine(_root, "data");
        var recentWorkspace = Path.Combine(data, "scratch", "imports", "recent");
        var hidden = WriteOldFile(Path.Combine(recentWorkspace, "hidden.txt"));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var staleWorkspace = Path.Combine(data, "scratch", "imports", "stale");
        WriteOldFile(Path.Combine(staleWorkspace, "old.txt"));
        AgeDirectories(_root);
        File.SetLastWriteTimeUtc(hidden, DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(recentWorkspace, Old);

        var report = await new RetentionCleanupService(new StorageOptions { AppDataDirectory = data }).CleanupAsync();
        Assert.True(File.Exists(hidden));
        Assert.False(Directory.Exists(staleWorkspace));
        Assert.Equal(1, report.DeletedScratchWorkspaceCount);
    }

    [Theory]
    [InlineData("logs", false)]
    [InlineData("logs/nested", false)]
    [InlineData("diagnostics", false)]
    [InlineData("diagnostics/nested", false)]
    [InlineData("logs", true)]
    public async Task Retention_PreservesFilesBehindJunction(string position, bool ancestor)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var data = Path.Combine(_root, "data");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = ancestor ? Path.Combine(_root, "redirect") : Path.Combine(data, position.Replace('/', Path.DirectorySeparatorChar));
        CreateJunction(link, outside);
        if (ancestor) data = Path.Combine(link, "data");
        var canary = WriteOldFile(Path.Combine(outside, ancestor ? "data/logs" : "", "old.log"));
        var service = new RetentionService(database.Factory, Options.Create(new RetentionOptions()),
            new StorageOptions { AppDataDirectory = data });
        var report = await service.RunAsync();
        Assert.True(File.Exists(canary));
        Assert.Equal(0, report.TotalFilesDeleted);
        Assert.True(report.TotalSkippedItems > 0);
    }

    [Fact]
    public async Task Retention_SkipsLinkedSubtreeAndStillDeletesOrdinaryStaleFiles()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var data = Path.Combine(_root, "data");
        var outside = Path.Combine(_root, "outside");
        var canary = WriteOldFile(Path.Combine(outside, "canary.log"));
        CreateJunction(Path.Combine(data, "logs", "linked"), outside);
        var oldFile = WriteOldFile(Path.Combine(data, "logs", "ordinary", "old.log"));
        var recentFile = WriteOldFile(Path.Combine(data, "logs", "ordinary", "recent.log"));
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow);
        var service = new RetentionService(database.Factory, Options.Create(new RetentionOptions()),
            new StorageOptions { AppDataDirectory = data });
        var report = await service.RunAsync();
        Assert.True(File.Exists(canary));
        Assert.True(File.Exists(recentFile));
        Assert.False(File.Exists(oldFile));
        Assert.Equal(1, report.TotalFilesDeleted);
        Assert.Equal(1, report.TotalSkippedItems);
    }

    [Fact]
    public async Task Cleanup_ArchiveJunctionPreservesAuditRowsAndDoesNotWriteOutside()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await database.SeedSessionAsync();
        await database.SeedExecutionAsync("terminal", state: "Succeeded", endedAt: new DateTimeOffset(Old));
        await database.InsertExecutionEventAsync("old", "terminal", 0, new DateTimeOffset(Old));
        var data = Path.Combine(_root, "data");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        CreateJunction(Path.Combine(data, "diagnostics", "archive"), outside);
        var service = new RetentionCleanupService(new StorageOptions { AppDataDirectory = data },
            connectionFactory: database.Factory);
        var report = await service.CleanupAsync();
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(1, await database.CountAsync("ExecutionEvents"));
        Assert.Equal(0, report.DeletedAuditRecordCount);
        Assert.NotEmpty(report.Warnings);
    }

    private static string WriteOldFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic retention canary");
        File.SetLastWriteTimeUtc(path, Old);
        return path;
    }

    private static void AgeDirectories(string root)
    {
        // Never traverse fixture junctions, including during test setup.
        foreach (var directory in Directory.GetDirectories(root))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
            {
                AgeDirectories(directory);
            }
        }

        Directory.SetLastWriteTimeUtc(root, Old);
    }

    private void CreateJunction(string link, string target)
    {
        EnsureFixturePath(link);
        EnsureFixturePath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:RETENTION_TEST_LINK -Target $env:RETENTION_TEST_TARGET | Out-Null");
        start.Environment["RETENTION_TEST_LINK"] = link;
        start.Environment["RETENTION_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30_000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        _links.Add(link);
        Assert.Equal(Path.GetFullPath(target), new DirectoryInfo(link).ResolveLinkTarget(true)!.FullName);
    }

    private void EnsureFixturePath(string path)
    {
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar,
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        foreach (var link in _links.AsEnumerable().Reverse())
        {
            EnsureFixturePath(link);
            if (Directory.Exists(link))
            {
                Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(link, recursive: false);
            }
        }

        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(expectedParent, Path.GetDirectoryName(Path.GetFullPath(_root)), ignoreCase: true);
        Assert.StartsWith("LLMWorkGUI-retention-", Path.GetFileName(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
