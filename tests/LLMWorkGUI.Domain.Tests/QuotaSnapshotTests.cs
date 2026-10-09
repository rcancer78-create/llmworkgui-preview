using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class QuotaSnapshotTests
{
    [Fact]
    public void QuotaBucket_ComputesFractionsAndHardReserve_Accurately()
    {
        var bucket = new QuotaBucket(
            "requests_per_day",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 1000,
            usedValue: 800,
            remainingValue: 200,
            resetAt: DateTimeOffset.UtcNow.AddHours(4),
            hardReserve: 250);

        Assert.Equal(0.20, bucket.RemainingFraction);
        Assert.Equal(0.80, bucket.UsedFraction);
        Assert.True(bucket.HasHardReserveViolation); // 200 <= 250
    }

    [Fact]
    public void QuotaBucket_NoViolation_WhenRemainingAboveReserve()
    {
        var bucket = new QuotaBucket(
            "tokens_per_minute",
            QuotaLimitUnit.Tokens,
            QuotaLimitWindow.PerMinute,
            limitValue: 50000,
            usedValue: 10000,
            remainingValue: 40000,
            hardReserve: 5000);

        Assert.Equal(0.80, bucket.RemainingFraction);
        Assert.False(bucket.HasHardReserveViolation); // 40000 > 5000
    }

    [Theory]
    [InlineData(-1, 100, 100, 50)]
    [InlineData(100, -1, 100, 50)]
    [InlineData(100, 100, -1, 50)]
    [InlineData(100, 100, 100, -1)]
    public void QuotaBucket_RejectsNegativeValues(double limit, double used, double remaining, double reserve)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaBucket(
            "test_bucket",
            QuotaLimitUnit.Credits,
            QuotaLimitWindow.PerMonth,
            limitValue: limit < 0 ? limit : 100,
            usedValue: used < 0 ? used : 50,
            remainingValue: remaining < 0 ? remaining : 50,
            hardReserve: reserve < 0 ? reserve : 10));
    }

    [Fact]
    public void QuotaSnapshot_TrustedAndFresh_AllowsNumericScore()
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new QuotaBucket(
            "daily",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 100,
            remainingValue: 75);

        var snapshot = new QuotaSnapshot(
            "snap-1",
            "acc-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt: now.AddMinutes(-2),
            buckets: new[] { bucket });

        Assert.True(snapshot.IsTrusted);
        Assert.True(snapshot.IsFresh(now));
        Assert.True(snapshot.CanCalculateNumericScore(now));
    }

    [Theory]
    [InlineData(QuotaProvenance.Unknown)]
    [InlineData(QuotaProvenance.Unsupported)]
    [InlineData(QuotaProvenance.Stale)]
    [InlineData(QuotaProvenance.Error)]
    [InlineData(QuotaProvenance.LocallyCalculated)]
    [InlineData(QuotaProvenance.Estimated)]
    public void QuotaSnapshot_UntrustedOrUnverifiable_NeverAllowsNumericScore(QuotaProvenance provenance)
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new QuotaBucket(
            "daily",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 100,
            remainingValue: 75);

        var snapshot = new QuotaSnapshot(
            "snap-1",
            "acc-1",
            provenance,
            capturedAt: now,
            buckets: new[] { bucket });

        Assert.False(snapshot.CanCalculateNumericScore(now));
    }

    [Fact]
    public void QuotaSnapshot_StaleSnapshot_DoesNotAllowNumericScore()
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = new QuotaBucket(
            "daily",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 100,
            remainingValue: 75);

        // Captured 10 minutes ago, default TTL is 5 minutes
        var snapshot = new QuotaSnapshot(
            "snap-1",
            "acc-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt: now.AddMinutes(-10),
            buckets: new[] { bucket });

        Assert.True(snapshot.IsTrusted);
        Assert.False(snapshot.IsFresh(now));
        Assert.False(snapshot.CanCalculateNumericScore(now));
    }

    [Fact]
    public void FactoryMethods_CreateExpectedSnapshots()
    {
        var unknown = QuotaSnapshot.CreateUnknown("acc-1");
        Assert.Equal(QuotaProvenance.Unknown, unknown.Provenance);
        Assert.Empty(unknown.Buckets);

        var unsupported = QuotaSnapshot.CreateUnsupported("acc-1");
        Assert.Equal(QuotaProvenance.Unsupported, unsupported.Provenance);
        Assert.Empty(unsupported.Buckets);

        var error = QuotaSnapshot.CreateError("acc-1", "HTTP 429 Too Many Requests");
        Assert.Equal(QuotaProvenance.Error, error.Provenance);
        Assert.Equal("HTTP 429 Too Many Requests", error.ErrorMessage);
    }
}
