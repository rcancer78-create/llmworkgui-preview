using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Routing;

public sealed class RoutingQuotaTtlTests
{
    [Fact]
    public async Task QuotaFirst_UsesTheConfiguredSnapshotTtl()
    {
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-06T09:00:00Z"));
        var accounts = new InMemoryAccountRepository();
        var snapshots = new InMemoryQuotaSnapshotRepository();
        await accounts.SaveAsync(new Account(
            "acc-ttl", "prov-ttl", "TTL account", null, AuthState.Valid, 10, true,
            HealthState.Healthy, null, null, 2, null));
        await snapshots.SaveAsync(new QuotaSnapshot(
            "snapshot-ttl",
            "acc-ttl",
            QuotaProvenance.ExactProviderReported,
            time.GetUtcNow().AddMinutes(-2),
            [new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 100, 80)],
            "prov-ttl",
            "gpt-4o"));
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile(
            "prov-ttl", "TTL provider", BackendType.OpenCode, null, null,
            DataClassification.PrivateSource, true));
        var engine = new RoutingEngine(
            accounts,
            snapshots,
            timeProvider: time,
            providerProfileRepository: profiles,
            quotaOptions: Options.Create(new QuotaSchedulerOptions { DefaultTtl = TimeSpan.FromMinutes(1) }));

        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-ttl",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.QuotaFirst
        });

        Assert.False(decision.IsSuccess);
        Assert.Contains("fresh trusted quota", Assert.Single(decision.RejectedCandidates).Reason, StringComparison.Ordinal);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
