using LLMWorkGUI.Application.Workflows;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    [Fact]
    public async Task HostStopRetiresAdmissionAndCleansOnlyItsRetainedPrivateOwners()
    {
        using var f = await RuntimeFixture();
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureFailStopped BEFORE INSERT ON WorkflowAdaptationRuntimeChecks WHEN NEW.Phase='Stopped' BEGIN SELECT RAISE(ABORT,'synthetic stop audit failure'); END;");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        var lifecycle = Assert.IsAssignableFrom<IHostedService>(f.Registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StopAsync(default));
        Assert.Equal(1L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
        Assert.Equal(1L, await f.Db.CountAsync("Sessions", "ActiveExecutionId IS NOT NULL"));
        await MaterialSql(f.Db, "DROP TRIGGER FixtureFailStopped");
        Assert.Equal(0, await f.Registry.RetryOwnedCleanupAsync());
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Terminated'"));
        // Retirement is irreversible even after cleanup succeeds. No new native process may launch.
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Db.Root, "runs")));
    }

    [Fact]
    public async Task StopBeforeFirstAdmissionNeverCreatesANativeRuntime()
    {
        using var f = await RuntimeFixture();
        var lifecycle = Assert.IsAssignableFrom<IHostedService>(f.Registry);
        await lifecycle.StopAsync(default);
        await lifecycle.StopAsync(default);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners"));
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }
}
