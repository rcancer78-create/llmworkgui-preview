using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.Configuration;

public sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePositive(failures, options.RawBackendEventsRetentionDays, nameof(options.RawBackendEventsRetentionDays));
        ValidatePositive(failures, options.ProcessServerLogsRetentionDays, nameof(options.ProcessServerLogsRetentionDays));
        ValidatePositive(failures, options.DiagnosticBundlesRetentionDays, nameof(options.DiagnosticBundlesRetentionDays));
        ValidatePositive(failures, options.QuotaSnapshotsRetentionDays, nameof(options.QuotaSnapshotsRetentionDays));
        ValidatePositive(failures, options.QuotaSnapshotsDownsampleDays, nameof(options.QuotaSnapshotsDownsampleDays));
        ValidatePositive(failures, options.HealthAuditTransitionsRetentionDays, nameof(options.HealthAuditTransitionsRetentionDays));

        if (options.QuotaSnapshotsDownsampleDays >= options.QuotaSnapshotsRetentionDays)
        {
            failures.Add(
                $"{nameof(options.QuotaSnapshotsDownsampleDays)} must be smaller than {nameof(options.QuotaSnapshotsRetentionDays)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePositive(List<string> failures, int value, string propertyName)
    {
        if (value < 1)
        {
            failures.Add($"{propertyName} must be a positive number of days.");
        }
    }
}
