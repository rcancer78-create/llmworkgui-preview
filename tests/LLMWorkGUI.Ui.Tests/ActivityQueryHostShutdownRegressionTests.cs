using System;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ActivityQueryHostShutdownRegressionTests
{
    [Fact]
    public async Task HostStopWaitsForThePhysicalQueryAndAcceptedPendingWork()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var worker = host.Services.GetRequiredService<BoundedActivityQueryExecutor>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pendingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Enqueue(() => { entered.Set(); release.Wait(); });
        Task? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            worker.Enqueue(() => pendingRan.TrySetResult());
            stop = host.StopAsync();
            Assert.False(stop.IsCompleted, "Host reported shutdown while its query worker still owned a query.");
            Assert.False(pendingRan.Task.IsCompleted);
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pendingRan.Task.IsCompleted);
            Assert.Equal(2, worker.ExecutedCount);
        }
        finally
        {
            release.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ACancelledHostStopCannotClaimAnUnfinishedQueryWasDrained()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var worker = host.Services.GetRequiredService<BoundedActivityQueryExecutor>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        worker.Enqueue(() => { entered.Set(); release.Wait(); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var stop = host.StopAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, worker.ExecutedCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AStoppedHostRetiresQueryAdmission()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var worker = host.Services.GetRequiredService<BoundedActivityQueryExecutor>();
        await host.StopAsync();
        worker.Enqueue(() => { });
        Assert.Equal(0, worker.EnqueuedCount);
        Assert.Equal(1, worker.RefusedCount);
    }

    private static IHost BuildHost() => new HostBuilder()
        .ConfigureServices((_, services) => services.AddUnifiedWorkspaceShell()).Build();
}
