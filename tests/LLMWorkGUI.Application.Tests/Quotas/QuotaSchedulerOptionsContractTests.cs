using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Quotas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaSchedulerOptionsContractTests
{
    [Theory]
    [InlineData("DefaultTtl")]
    [InlineData("MinRefreshInterval")]
    [InlineData("ProviderRateLimitDelay")]
    [InlineData("InitialBackoff")]
    [InlineData("MaxBackoff")]
    [InlineData("BackoffOrder")]
    [InlineData("BackoffNaN")]
    [InlineData("BackoffInfinity")]
    [InlineData("BackoffShrinks")]
    [InlineData("JitterNaN")]
    [InlineData("JitterInfinity")]
    [InlineData("JitterNegative")]
    [InlineData("JitterAboveOne")]
    [InlineData("BackgroundPollInterval")]
    public void Constructor_RejectsInvalidSchedulingConfigurationBeforeWork(string invalidField)
    {
        var options = new QuotaSchedulerOptions();
        switch (invalidField)
        {
            case "DefaultTtl": options.DefaultTtl = TimeSpan.Zero; break;
            case "MinRefreshInterval": options.MinRefreshInterval = TimeSpan.FromTicks(-1); break;
            case "ProviderRateLimitDelay": options.ProviderRateLimitDelay = TimeSpan.FromTicks(-1); break;
            case "InitialBackoff": options.InitialBackoff = TimeSpan.Zero; break;
            case "MaxBackoff": options.MaxBackoff = TimeSpan.Zero; break;
            case "BackoffOrder": options.MaxBackoff = options.InitialBackoff - TimeSpan.FromTicks(1); break;
            case "BackoffNaN": options.BackoffMultiplier = double.NaN; break;
            case "BackoffInfinity": options.BackoffMultiplier = double.PositiveInfinity; break;
            case "BackoffShrinks": options.BackoffMultiplier = 0.5; break;
            case "JitterNaN": options.JitterRatio = double.NaN; break;
            case "JitterInfinity": options.JitterRatio = double.PositiveInfinity; break;
            case "JitterNegative": options.JitterRatio = -0.1; break;
            case "JitterAboveOne": options.JitterRatio = 1.1; break;
            case "BackgroundPollInterval": options.BackgroundPollInterval = TimeSpan.Zero; break;
        }

        // Options.Create intentionally bypasses DI validators; the public constructor must enforce
        // the same contract before it can start a polling epoch or accept an adapter request.
        var error = Assert.Throws<OptionsValidationException>(() => new QuotaRefreshScheduler(
            new MockQuotaSourceAdapter(), new InMemoryQuotaSnapshotRepository(),
            options: Options.Create(options)));
        Assert.NotEmpty(error.Failures);
    }

    [Fact]
    public void AddApplication_RejectsInvalidBoundQuotaConfigurationWhenOptionsAreResolved()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["QuotaScheduler:BackgroundPollInterval"] = "00:00:00" })
            .Build();
        var services = new ServiceCollection().AddApplication(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<QuotaSchedulerOptions>>().Value);
        Assert.Contains(error.Failures, item => item.Contains(
            nameof(QuotaSchedulerOptions.BackgroundPollInterval), StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_AllowsZeroOptionalDelaysAndConstantBackoff()
    {
        using var scheduler = new QuotaRefreshScheduler(new MockQuotaSourceAdapter(),
            new InMemoryQuotaSnapshotRepository(), options: Options.Create(new QuotaSchedulerOptions
            {
                MinRefreshInterval = TimeSpan.Zero, ProviderRateLimitDelay = TimeSpan.Zero,
                JitterRatio = 0, BackoffMultiplier = 1
            }));

        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.ComputeBackoff(20));
        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.ApplyJitter(TimeSpan.FromMinutes(5)));
    }
}
