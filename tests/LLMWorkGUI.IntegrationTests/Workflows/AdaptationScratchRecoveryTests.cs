using System.Diagnostics;
using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class AdaptationScratchRecoveryTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private ScratchWorkspaceManager Manager() => new(new WorkflowBlobStore(_directory.Root), new SafeArchiveValidator());
    private string RecordPath => Directory.GetFiles(_directory.GetPath("adaptation-scratch-owners"), "*.json").Single();
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task ExistingNonDirectoryPathIsNotReportedAsCleaned()
    {
        var path = _directory.GetPath("workspace-replaced-by-file");
        await File.WriteAllTextAsync(path, "must not be forgotten");
        var ownershipReleased = false;
        var workspace = new ScratchWorkspace(path, ScratchScope.Adaptation, "fixture",
            afterCleanup: () => ownershipReleased = true);
        await Assert.ThrowsAnyAsync<IOException>(() => workspace.CleanupWorkspaceAsync());
        Assert.False(ownershipReleased);
        Assert.False(workspace.IsCleanedUp);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LiveOwnerSurvivesNewManagerAndAgeBasedRetention()
    {
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        Assert.True(File.Exists(RecordPath));
        Directory.SetLastWriteTimeUtc(workspace.DirectoryPath, DateTime.UtcNow.AddDays(-30));
        Assert.Equal(0, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
        var retention = new RetentionCleanupService(new StorageOptions { AppDataDirectory = _directory.Root });
        var report = await retention.CleanupAsync();
        Assert.Equal(0, report.DeletedScratchWorkspaceCount);
        Assert.True(Directory.Exists(workspace.DirectoryPath));
        await workspace.CleanupWorkspaceAsync();
        Assert.Empty(Directory.GetFiles(_directory.GetPath("adaptation-scratch-owners")));
    }

    [Fact]
    public async Task ExitedOwnerIsRecoveredAndLockedFailureSurvivesForRetry()
    {
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        var file = Path.Combine(workspace.DirectoryPath, "candidate.txt");
        await File.WriteAllTextAsync(file, "synthetic candidate");
        await MarkOwnerExitedAsync();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
            Assert.True(File.Exists(RecordPath));
            Assert.True(Directory.Exists(workspace.DirectoryPath));
        }
        Assert.Equal(0, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
        Assert.False(Directory.Exists(workspace.DirectoryPath));
        Assert.Empty(Directory.GetFiles(_directory.GetPath("adaptation-scratch-owners")));
        Assert.Equal(0, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":1,\"DirectoryName\":\"../../outside\",\"ProcessId\":1,\"StartTimeUtcTicks\":1}")]
    public async Task CorruptOrOutsideRecordNeverAuthorizesDeletion(string payload)
    {
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        var record = RecordPath;
        await File.WriteAllTextAsync(record, payload);
        Assert.Equal(1, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
        Assert.Equal(payload, await File.ReadAllTextAsync(record));
        Assert.True(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task StartupActuallyInvokesRecoveryForExitedOwner()
    {
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        await MarkOwnerExitedAsync();
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        try
        {
            await HostBootstrapper.InitializeAsync(host);
            Assert.False(Directory.Exists(workspace.DirectoryPath));
        }
        finally
        {
            TestSqlitePool.Clear(new LLMWorkGUI.Infrastructure.Data.SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        }
    }

    [Fact]
    public async Task LinkedChildPreventsRecoveryAndPreservesOutsideFile()
    {
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        var outside = _directory.GetPath("outside.txt");
        await File.WriteAllTextAsync(outside, "must survive");
        var link = Path.Combine(workspace.DirectoryPath, "link.txt");
        File.CreateSymbolicLink(link, outside);
        try
        {
            await MarkOwnerExitedAsync();
            Assert.Equal(1, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
            Assert.Equal("must survive", await File.ReadAllTextAsync(outside));
            Assert.True(File.Exists(RecordPath));
        }
        finally { File.Delete(link); }
        Assert.Equal(0, await Manager().RecoverAbandonedAdaptationWorkspacesAsync());
    }

    private async Task MarkOwnerExitedAsync()
    {
        // Simulate a record left across restart using an actual exited process identity.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        var started = process.StartTime.ToUniversalTime().Ticks;
        await process.StandardInput.WriteLineAsync();
        await process.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        var owner = JsonSerializer.Deserialize<AdaptationScratchOwnership.Owner>(await File.ReadAllTextAsync(RecordPath))!;
        await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(owner with { ProcessId = process.Id, StartTimeUtcTicks = started }));
    }

    [Fact]
    public async Task ViewOnlyStartupDoesNotRecoverExitedOwners()
    {
        using (var primary = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root))
            await HostBootstrapper.InitializeAsync(primary);
        var workspace = await Manager().CreateWorkspaceAsync(ScratchScope.Adaptation, "session");
        await MarkOwnerExitedAsync();
        using var secondary = HostBootstrapper.CreateHostBuilder(appDataDirectory: _directory.Root)
            .ConfigureServices((_, services) => services.AddSingleton<IApplicationInstanceGuard>(new ViewOnlyGuard())).Build();
        try
        {
            await HostBootstrapper.InitializeAsync(secondary);
            Assert.True(Directory.Exists(workspace.DirectoryPath));
            Assert.True(File.Exists(RecordPath));
        }
        finally
        {
            TestSqlitePool.Clear(new LLMWorkGUI.Infrastructure.Data.SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        }
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "scratch-view-only";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException();
        public void Dispose() { }
    }
}
