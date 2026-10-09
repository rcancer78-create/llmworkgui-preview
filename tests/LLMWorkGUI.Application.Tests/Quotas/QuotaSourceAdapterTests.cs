using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaSourceAdapterTests
{
    [Fact]
    public async Task MockQuotaSourceAdapter_ReturnsExpectedSnapshots_PerAccountBehavior()
    {
        var adapter = new MockQuotaSourceAdapter();

        // 1. Default account (80% remaining, ExactProviderReported)
        var defaultSnap = await adapter.FetchQuotaAsync("prov-1", "acc-standard");
        Assert.Equal(QuotaProvenance.ExactProviderReported, defaultSnap.Provenance);
        Assert.NotNull(defaultSnap.PrimaryBucket);
        Assert.Equal(0.80, defaultSnap.PrimaryBucket!.RemainingFraction);
        Assert.False(defaultSnap.HasHardReserveViolation);

        // 2. Unsupported account
        var unsupportedSnap = await adapter.FetchQuotaAsync("prov-1", "acc-unsupported");
        Assert.Equal(QuotaProvenance.Unsupported, unsupportedSnap.Provenance);
        Assert.Empty(unsupportedSnap.Buckets);

        // 3. Error account
        var errorSnap = await adapter.FetchQuotaAsync("prov-1", "acc-error");
        Assert.Equal(QuotaProvenance.Error, errorSnap.Provenance);
        Assert.NotNull(errorSnap.ErrorMessage);
        Assert.Contains("429", errorSnap.ErrorMessage);

        // 4. Reserve violation account
        var violationSnap = await adapter.FetchQuotaAsync("prov-1", "acc-reserve-violation");
        Assert.Equal(QuotaProvenance.ExactProviderReported, violationSnap.Provenance);
        Assert.True(violationSnap.HasHardReserveViolation);

        // 5. Configured snapshot override
        var custom = new QuotaSnapshot(
            "snap-custom",
            "acc-custom",
            QuotaProvenance.PluginReported,
            DateTimeOffset.UtcNow,
            new[] { new QuotaBucket("custom", QuotaLimitUnit.Credits, QuotaLimitWindow.PerMonth, 100, 10, 90) });

        adapter.SetSnapshot("acc-custom", custom);
        var fetchedCustom = await adapter.FetchQuotaAsync("prov-1", "acc-custom");
        Assert.Same(custom, fetchedCustom);
    }

    [Fact]
    public async Task OpenCodePluginQuotaAdapter_ReturnsUnsupported_WhenNoPluginConfigured()
    {
        var adapter = new OpenCodePluginQuotaAdapter(hasQuotaPluginContract: false);

        Assert.Equal("opencode-plugin-quota", adapter.SourceKind);

        var snap = await adapter.FetchQuotaAsync("prov-opencode", "acc-1");
        Assert.Equal(QuotaProvenance.Unsupported, snap.Provenance);
        Assert.Empty(snap.Buckets);
    }
}
