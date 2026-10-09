using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.Quotas;

public sealed class QuotaSchedulerOptionsValidator : IValidateOptions<QuotaSchedulerOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotaSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.DefaultTtl <= TimeSpan.Zero)
            failures.Add($"{nameof(options.DefaultTtl)} must be positive.");
        if (options.MinRefreshInterval < TimeSpan.Zero)
            failures.Add($"{nameof(options.MinRefreshInterval)} must not be negative.");
        if (options.ProviderRateLimitDelay < TimeSpan.Zero)
            failures.Add($"{nameof(options.ProviderRateLimitDelay)} must not be negative.");
        if (options.InitialBackoff <= TimeSpan.Zero)
            failures.Add($"{nameof(options.InitialBackoff)} must be positive.");
        if (options.MaxBackoff <= TimeSpan.Zero || options.MaxBackoff < options.InitialBackoff)
            failures.Add($"{nameof(options.MaxBackoff)} must be positive and at least {nameof(options.InitialBackoff)}.");
        if (!double.IsFinite(options.BackoffMultiplier) || options.BackoffMultiplier < 1)
            failures.Add($"{nameof(options.BackoffMultiplier)} must be finite and at least one.");
        if (!double.IsFinite(options.JitterRatio) || options.JitterRatio < 0 || options.JitterRatio > 1)
            failures.Add($"{nameof(options.JitterRatio)} must be finite and between zero and one.");
        if (options.BackgroundPollInterval <= TimeSpan.Zero)
            failures.Add($"{nameof(options.BackgroundPollInterval)} must be positive.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
