using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Observability;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

public sealed class ActivityBridgeFailureTests
{
    [Fact]
    public void RepeatedNotificationsAtSameClockTickHaveDistinctEventIds()
    {
        var activity = new ActivityCenterService(text => text);
        using var source = new QuotaSource();
        using var bridge = new ActivityCenterEventBridge(activity, [source], new FrozenClock());
        source.Raise(); source.Raise();
        var events = activity.Query().Items.Where(item => item.Kind == ActivityEventKind.Health).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(2, events.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void FailedIngestionCannotBreakProducerOrRecurse()
    {
        var activity = new ActivityCenterService(_ => throw new IOException("synthetic failure"));
        using var source = new QuotaSource();
        using var bridge = new ActivityCenterEventBridge(activity, [source]);
        source.Raise();
        Assert.Equal(2, bridge.FailedIngestions);
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class QuotaSource : IQuotaRefreshScheduler
    {
        public event EventHandler<QuotaRefreshedEventArgs>? QuotaRefreshed;
        public void Raise() => QuotaRefreshed?.Invoke(this, new QuotaRefreshedEventArgs("account", "provider",
            new QuotaSnapshot("snapshot", "account", QuotaProvenance.Unknown, DateTimeOffset.UnixEpoch), true));
        public Task<QuotaSnapshot> RefreshAccountNowAsync(string providerProfileId, string accountId, string? modelId = null,
            bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshAllEligibleAccountsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public AccountQuotaRefreshStatus? GetStatus(string accountId) => null;
        public IReadOnlyList<AccountQuotaRefreshStatus> GetAllStatuses() => [];
        public void ScheduleNextRefresh(string accountId, string providerProfileId, DateTimeOffset? expiresAt = null, bool isFailure = false) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
