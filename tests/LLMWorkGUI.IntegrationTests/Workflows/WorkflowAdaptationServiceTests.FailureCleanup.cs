using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Fact]
    public async Task SuccessfulPreviewWithLockedCleanupReportsPendingWithoutReturningPreview()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        FileStream? locked = null;
        _scratchManager.AfterExtractAsync = _ =>
        {
            locked = new FileStream(Path.Combine(GetAdaptationScratchDirectories().Single(), "README.md"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        };
        try
        {
            var failure = await Assert.ThrowsAnyAsync<IOException>(() => _service.PreparePreSendPreviewAsync(
                version.Id, "acct-1", AdaptationGoal.Balanced));
            Assert.Equal(true, failure.Data["ScratchCleanupPending"]);
            Assert.Single(Directory.GetFiles(Path.Combine(_database.Root, "adaptation-scratch-owners"), "*.json"));
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialTurnPreservesPrimaryFailureAndAttemptsBothWorkspaces(bool cancelled)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        Exception primary = cancelled ? new OperationCanceledException("original cancellation")
            : new InvalidOperationException("original model failure");
        FileStream? locked = null;
        string? candidate = null;
        string? source = null;
        var service = ConcurrentService(new AsyncInvoker((_, _) =>
        {
            candidate = GetAdaptationScratchDirectories().Single(p => Path.GetFileName(p).StartsWith("session-", StringComparison.Ordinal));
            source = GetAdaptationScratchDirectories().Single(p => Path.GetFileName(p).StartsWith("source-", StringComparison.Ordinal));
            locked = new FileStream(Path.Combine(candidate, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromException<AdaptationModelResponse>(primary);
        }));
        try
        {
            var actual = await Record.ExceptionAsync(() => service.StartAdaptationAsync(
                new(version.Id, "acct-1", AdaptationGoal.Balanced)));
            Assert.Same(primary, actual);
            Assert.Equal(true, actual!.Data["ScratchCleanupPending"]);
            Assert.True(Directory.Exists(candidate));
            Assert.False(Directory.Exists(source));
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreviewPreservesPrimaryFailureWhenScratchIsLocked(bool cancelled)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "original")));
        Exception primary = cancelled ? new OperationCanceledException("original cancellation")
            : new IOException("original extraction failure");
        FileStream? locked = null;
        _scratchManager.AfterExtractAsync = _ =>
        {
            locked = new FileStream(Path.Combine(GetAdaptationScratchDirectories().Single(), "README.md"),
                FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromException(primary);
        };
        try
        {
            var actual = await Record.ExceptionAsync(() => _service.PreparePreSendPreviewAsync(
                version.Id, "acct-1", AdaptationGoal.Balanced));
            Assert.Same(primary, actual);
            Assert.Equal(true, actual!.Data["ScratchCleanupPending"]);
        }
        finally { locked?.Dispose(); }
    }
}
