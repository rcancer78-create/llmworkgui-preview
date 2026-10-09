using System.Diagnostics;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowImportPathSwapReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryImportRechecksAnObservedPathBeforeOpeningItsActualBytes(bool swap)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var directory = new TestDirectory();
        var root = directory.GetPath("source");
        var subtree = Path.Combine(root, "prompts");
        var target = directory.GetPath("outside");
        var preserved = directory.GetPath("preserved");
        Directory.CreateDirectory(subtree);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(subtree, "executor.md"), "original owned input");
        const string external = "owned outside sentinel must not be imported";
        await File.WriteAllTextAsync(Path.Combine(target, "executor.md"), external);
        var opened = 0;
        var changed = false;
        var service = new WorkflowImportService(new SqliteWorkflowPackageRepository(database.Factory),
            new SqliteWorkflowVersionRepository(database.Factory), new WorkflowBlobStore(database.Root),
            new SafeArchiveValidator(), TimeProvider.System, NullLogger<WorkflowImportService>.Instance,
            new WorkflowManifestParser())
        {
            DirectoryTestHooks = new(BeforeFileCopy: _ =>
            {
                if (!swap || changed) return;
                Assert.StartsWith(Path.GetFullPath(directory.Root) + Path.DirectorySeparatorChar,
                    Path.GetFullPath(subtree), StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(Path.GetFullPath(directory.Root) + Path.DirectorySeparatorChar,
                    Path.GetFullPath(preserved), StringComparison.OrdinalIgnoreCase);
                Directory.Move(subtree, preserved);
                var start = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:IMPORT_TEST_LINK -Target $env:IMPORT_TEST_TARGET | Out-Null");
                start.Environment["IMPORT_TEST_LINK"] = subtree;
                start.Environment["IMPORT_TEST_TARGET"] = target;
                using var process = Process.Start(start)!;
                Assert.True(process.WaitForExit(30_000));
                Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
                changed = true;
                Assert.True((File.GetAttributes(subtree) & FileAttributes.ReparsePoint) != 0);
            }, OpenSource: path =>
            {
                opened++;
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            })
        };
        try
        {
            if (swap)
            {
                await Assert.ThrowsAsync<WorkflowValidationException>(() => service.ImportDirectoryAsync(root, "path swap"));
                Assert.True(changed);
                Assert.Equal(0, opened);
                Assert.Equal(0, await database.CountAsync("WorkflowPackages"));
                Assert.Equal(0, await database.CountAsync("WorkflowVersions"));
            }
            else
            {
                await service.ImportDirectoryAsync(root, "unchanged input");
                Assert.Equal(1, opened);
                Assert.Equal(1, await database.CountAsync("WorkflowPackages"));
                Assert.Equal(1, await database.CountAsync("WorkflowVersions"));
            }
            Assert.Equal(external, await File.ReadAllTextAsync(Path.Combine(target, "executor.md")));
        }
        finally
        {
            if (changed)
            {
                Assert.True((File.GetAttributes(subtree) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(subtree, recursive: false);
            }
        }
    }
}
