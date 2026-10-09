using LLMWorkGUI.Infrastructure.Concurrency;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed partial class CheckoutLockServiceTests
{
    [Fact]
    public async Task FailedDatabaseReleaseKeepsTheNamedMutexUntilASuccessfulRetry()
    {
        using var harness = await CreateHarnessAsync();
        var root = _database.GetWorkspacePath();
        var token = await harness.Service.AcquireWriterLockAsync("project-1", root, "execution-1", 1);
        try
        {
            await _database.UpdateExecutionStateAsync("execution-1", "Ambiguous");
            await Assert.ThrowsAsync<LLMWorkGUI.Domain.Entities.ProjectLockConflictException>(
                () => token.ReleaseAsync("release refused"));

            Assert.True(token.IsHeld);
            Assert.NotNull(await harness.Repository.GetActiveByRootPathAsync(root));
            var acquiredElsewhere = RunOnDedicatedThread(() =>
            {
                using var mutex = new Mutex(false, NamedMutexNames.ForCheckout(root));
                var acquired = mutex.WaitOne(0);
                if (acquired) mutex.ReleaseMutex();
                return acquired;
            });
            Assert.False(acquiredElsewhere);
        }
        finally
        {
            await _database.UpdateExecutionStateAsync("execution-1", "Succeeded");
            await token.ReleaseAsync("confirmed terminal cleanup");
        }
        Assert.False(token.IsHeld);
        Assert.Null(await harness.Repository.GetActiveByRootPathAsync(root));
    }
}
