using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessSupervisorRegistrationTests
{
    [Fact]
    public void Validate_NegativeStartupGraceWindow_RejectsInvalidWaitBoundary()
    {
        var result = new ProcessSupervisorOptionsValidator().Validate(null,
            new ProcessSupervisorOptions { StartupGraceWindow = TimeSpan.FromTicks(-1) });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(
            nameof(ProcessSupervisorOptions.StartupGraceWindow), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroStartupGraceWindow_AllowsImmediateOwnedCleanupBoundary()
    {
        var result = new ProcessSupervisorOptionsValidator().Validate(null,
            new ProcessSupervisorOptions { StartupGraceWindow = TimeSpan.Zero });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AddApplicationAndInfrastructure_RegisterProcessSupervisorWithTypedOptions()
    {
        using var dataDirectory = new TestDirectory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var supervisor = provider.GetRequiredService<IProcessSupervisor>();
        var guarded = Assert.IsType<GuardedProcessSupervisor>(supervisor);
        Assert.IsType<ProcessSupervisor>(guarded.Inner);

        var options = provider.GetRequiredService<IOptions<ProcessSupervisorOptions>>().Value;
        Assert.Equal(ProcessSupervisorOptions.DefaultOutputMemoryLimitBytes, options.OutputMemoryLimitBytes);
        Assert.Equal(ProcessSupervisorOptions.DefaultOutputHeadRetentionBytes, options.OutputHeadRetentionBytes);
        Assert.Equal(ProcessSupervisorOptions.DefaultOutputTailRetentionBytes, options.OutputTailRetentionBytes);
        Assert.True(options.StartupTimeout > TimeSpan.Zero);
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var validator = new ProcessSupervisorOptionsValidator();

        var result = validator.Validate(null, new ProcessSupervisorOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InvalidOptions_ReportsFailures()
    {
        var validator = new ProcessSupervisorOptionsValidator();

        var options = new ProcessSupervisorOptions
        {
            OutputMemoryLimitBytes = 1024,
            OutputHeadRetentionBytes = 1024,
            OutputTailRetentionBytes = 1024,
            OutputChannelCapacity = 0,
            StreamReadBufferSize = 4,
            StartupTimeout = TimeSpan.Zero,
            TurnTimeout = TimeSpan.Zero,
            InactivityTimeout = TimeSpan.Zero,
            GracefulShutdownTimeout = TimeSpan.FromSeconds(-1)
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains(nameof(ProcessSupervisorOptions.OutputMemoryLimitBytes), StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains(nameof(ProcessSupervisorOptions.StartupTimeout), StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains(nameof(ProcessSupervisorOptions.OutputChannelCapacity), StringComparison.Ordinal));
    }
}
