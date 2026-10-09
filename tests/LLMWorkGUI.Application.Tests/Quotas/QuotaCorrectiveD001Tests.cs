using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaCorrectiveD001Tests
{
    [Theory]
    [InlineData(50)]
    [InlineData(250)]
    public void SubsecondConfiguredBackoffKeepsItsDeclaredJitterBounds(int milliseconds)
    {
        var options = new QuotaSchedulerOptions
        {
            InitialBackoff = TimeSpan.FromMilliseconds(milliseconds),
            MaxBackoff = TimeSpan.FromMilliseconds(milliseconds),
            JitterRatio = 0.1
        };
        using var scheduler = new QuotaRefreshScheduler(new MockQuotaSourceAdapter(),
            new InMemoryQuotaSnapshotRepository(), options: Options.Create(options), randomSeed: 42);
        var backoff = scheduler.ComputeBackoff(8);
        for (var index = 0; index < 10; index++)
            Assert.InRange(scheduler.ApplyJitter(backoff).TotalMilliseconds, milliseconds * 0.9, milliseconds * 1.1);
    }

    [Fact]
    public void ValidMaximumIntervalJitterCannotOverflowTimeSpan()
    {
        using var scheduler = new QuotaRefreshScheduler(new MockQuotaSourceAdapter(),
            new InMemoryQuotaSnapshotRepository(), options: Options.Create(new QuotaSchedulerOptions
                { JitterRatio = 1 }), randomSeed: 42);
        var interval = scheduler.ApplyJitter(TimeSpan.MaxValue);
        Assert.True(interval > TimeSpan.Zero);
        Assert.True(interval <= TimeSpan.MaxValue);
    }

    [Fact]
    public async Task BulkRefreshCannotReportSuccessWhenCancelledAfterItsFirstDurableObservation()
    {
        var accounts = new InMemoryAccountRepository();
        foreach (var id in new[] { "first", "second" })
            await accounts.SaveAsync(new Account(id, "profile-1", id, null, AuthState.Valid, 1,
                true, HealthState.Healthy, null, null, 1, null));
        var adapter = new MockQuotaSourceAdapter();
        var snapshots = new InMemoryQuotaSnapshotRepository();
        using var scheduler = new QuotaRefreshScheduler(adapter, snapshots, accounts,
            Options.Create(new QuotaSchedulerOptions { JitterRatio = 0 }));
        using var cancellation = new CancellationTokenSource();
        scheduler.QuotaRefreshed += (_, _) => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.RefreshAllEligibleAccountsAsync(cancellation.Token));

        Assert.Equal(1, adapter.FetchCallCount);
        Assert.NotNull(await snapshots.GetLatestForAccountAsync("first"));
        Assert.Null(await snapshots.GetLatestForAccountAsync("second"));
    }
}
