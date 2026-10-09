using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class ScratchFailureContractReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCleanupCallbackFailurePreservesTheOriginalCancellationAndAllowsRetry(bool afterDelete)
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("scratch");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "owned.txt"), "test-owned scratch");
        var fail = true;
        void Callback()
        {
            if (fail) throw new InvalidOperationException("synthetic public cleanup callback failure");
        }
        var workspace = new ScratchWorkspace(path, ScratchScope.Preview, "test-public-callback",
            beforeCleanup: afterDelete ? null : Callback, afterCleanup: afterDelete ? Callback : null);
        var primary = new OperationCanceledException("original request cancellation");

        var replacementFailure = await Record.ExceptionAsync(() => workspace.CleanupAfterFailureAsync(primary));

        Assert.Null(replacementFailure);
        Assert.Equal(true, primary.Data["ScratchCleanupPending"]);
        Assert.False(workspace.IsCleanedUp);
        Assert.Equal(!afterDelete, Directory.Exists(path));
        fail = false;
        await workspace.CleanupWorkspaceAsync();
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(path));
        // This public callback fixture asserts no native process or durable Adaptation owner proof.
    }
}
