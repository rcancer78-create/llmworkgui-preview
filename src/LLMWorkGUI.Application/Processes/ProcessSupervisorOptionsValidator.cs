using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.Processes;

public sealed class ProcessSupervisorOptionsValidator : IValidateOptions<ProcessSupervisorOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.OutputMemoryLimitBytes < 1)
        {
            failures.Add($"{nameof(options.OutputMemoryLimitBytes)} must be positive.");
        }

        if (options.ProtocolStandardErrorSpoolLimitBytes < 1)
        {
            failures.Add($"{nameof(options.ProtocolStandardErrorSpoolLimitBytes)} must be positive.");
        }

        if (options.OutputHeadRetentionBytes < 0)
        {
            failures.Add($"{nameof(options.OutputHeadRetentionBytes)} must not be negative.");
        }

        if (options.OutputTailRetentionBytes < 0)
        {
            failures.Add($"{nameof(options.OutputTailRetentionBytes)} must not be negative.");
        }

        if ((long)options.OutputHeadRetentionBytes + options.OutputTailRetentionBytes
            > options.OutputMemoryLimitBytes)
        {
            failures.Add(
                $"{nameof(options.OutputHeadRetentionBytes)} plus {nameof(options.OutputTailRetentionBytes)} must not exceed {nameof(options.OutputMemoryLimitBytes)}.");
        }

        if (options.OutputChannelCapacity < 1)
        {
            failures.Add($"{nameof(options.OutputChannelCapacity)} must be positive.");
        }

        if (options.StreamReadBufferSize < 16)
        {
            failures.Add($"{nameof(options.StreamReadBufferSize)} must be at least 16 characters.");
        }

        if (options.StartupTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.StartupTimeout)} must be positive.");
        }

        if (options.TurnTimeout is { } turnTimeout && turnTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.TurnTimeout)} must be positive when specified.");
        }

        if (options.InactivityTimeout is { } inactivityTimeout && inactivityTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.InactivityTimeout)} must be positive when specified.");
        }

        if (options.GracefulShutdownTimeout < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.GracefulShutdownTimeout)} must not be negative.");
        }

        if (options.StartupGraceWindow < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.StartupGraceWindow)} must not be negative.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
