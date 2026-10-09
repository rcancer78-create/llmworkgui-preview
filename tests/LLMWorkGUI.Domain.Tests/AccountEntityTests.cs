using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class AccountEntityTests
{
    [Fact]
    public void Constructor_SetsAllProperties_WhenValid()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new Account(
            "acc-1",
            "provider-1",
            "Production Account",
            "native-123",
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            now.AddMinutes(5),
            now.AddMinutes(10),
            4,
            0.15,
            secretReference: "urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001");

        Assert.Equal("acc-1", account.Id);
        Assert.Equal("provider-1", account.ProviderProfileId);
        Assert.Equal("Production Account", account.DisplayName);
        Assert.Equal("native-123", account.ProviderNativeId);
        Assert.Equal(AuthState.Valid, account.AuthState);
        Assert.Equal(10, account.ManualPriority);
        Assert.True(account.IsEnabled);
        Assert.Equal(HealthState.Healthy, account.Health);
        Assert.Equal(now.AddMinutes(5), account.CooldownUntil);
        Assert.Equal(now.AddMinutes(10), account.DisabledUntil);
        Assert.Equal(4, account.MaxConcurrentExecutions);
        Assert.Equal(0.15, account.ReserveThreshold);
        Assert.Equal("urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001", account.SecretReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankId(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => new Account(
            invalidId,
            "provider-1",
            "Account",
            null,
            AuthState.Valid,
            0,
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            null));
    }

    [Fact]
    public void Constructor_RejectsNegativeManualPriority()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Account(
            "acc-1",
            "provider-1",
            "Account",
            null,
            AuthState.Valid,
            -1,
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            null));
    }

    [Fact]
    public void Constructor_RejectsZeroMaxConcurrentExecutions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Account(
            "acc-1",
            "provider-1",
            "Account",
            null,
            AuthState.Valid,
            0,
            true,
            HealthState.Healthy,
            null,
            null,
            0,
            null));
    }

    [Fact]
    public void Constructor_RejectsNegativeReserveThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Account(
            "acc-1",
            "provider-1",
            "Account",
            null,
            AuthState.Valid,
            0,
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            -0.05));
    }

    [Fact]
    public void Constructor_RejectsInvalidSecretReferenceFormat()
    {
        Assert.Throws<ArgumentException>(() => new Account(
            "acc-1",
            "provider-1",
            "Account",
            null,
            AuthState.Valid,
            0,
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            null,
            secretReference: "invalid:secret:reference"));
    }

    [Fact]
    public void IsEligibleForRouting_ReturnsExpectedBoolean()
    {
        var now = DateTimeOffset.UtcNow;

        var eligible = new Account(
            "acc-1", "provider-1", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy,
            null, null, 1, null);

        Assert.True(eligible.IsEligibleForRouting(now));

        // Disabled
        Assert.False(eligible.WithIsEnabled(false).IsEligibleForRouting(now));

        // Invalid Auth
        Assert.False(eligible.WithAuthState(AuthState.Invalid).IsEligibleForRouting(now));
        Assert.False(eligible.WithAuthState(AuthState.Unknown).IsEligibleForRouting(now));

        // Quarantined
        Assert.False(eligible.WithHealth(HealthState.QuarantinedAuto).IsEligibleForRouting(now));
        Assert.False(eligible.WithHealth(HealthState.CoolingDown).IsEligibleForRouting(now));

        // Active Cooldown
        var cooling = eligible.WithCooldown(now.AddMinutes(5));
        Assert.False(cooling.IsEligibleForRouting(now));

        // Expired Cooldown
        var expiredCooldown = eligible.WithCooldown(now.AddMinutes(-1));
        Assert.True(expiredCooldown.IsEligibleForRouting(now));
    }

    [Fact]
    public void WithMethods_PreserveOtherProperties()
    {
        var account = new Account(
            "acc-1", "provider-1", "Account", "native-1",
            AuthState.Valid, 5, true, HealthState.Healthy,
            null, null, 2, 0.1,
            secretReference: "urn:llmworkgui:secret:11111111-1111-1111-1111-111111111111");

        var modified = account
            .WithAuthState(AuthState.Refreshing)
            .WithHealth(HealthState.Degraded)
            .WithManualPriority(20);

        Assert.Equal("acc-1", modified.Id);
        Assert.Equal("provider-1", modified.ProviderProfileId);
        Assert.Equal("Account", modified.DisplayName);
        Assert.Equal("native-1", modified.ProviderNativeId);
        Assert.Equal(AuthState.Refreshing, modified.AuthState);
        Assert.Equal(HealthState.Degraded, modified.Health);
        Assert.Equal(20, modified.ManualPriority);
        Assert.Equal("urn:llmworkgui:secret:11111111-1111-1111-1111-111111111111", modified.SecretReference);
    }
}
