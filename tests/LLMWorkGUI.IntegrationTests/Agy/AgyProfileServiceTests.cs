using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.Agy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Agy;

public sealed class AgyProfileServiceTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    private const string AgyProfileCmdPath = @"C:\Users\user\AppData\Local\agy-profile\agy-profile.cmd";

    private const string AgyProfilePs1Path = @"C:\Users\user\AppData\Local\agy-profile\agy-profile.ps1";

    private const string ListOutput =
        "Saved profiles ('*' = matches the logged-in account):\n" +
        "  * work                 2026-09-22T10:00:00\n" +
        "    personal             2026-09-23T08:30:00\n";

    [Fact]
    public void Availability_WhenResolverFindsUtility_IsReportedWithoutBlocker()
    {
        var service = CreateService(AgyProfileCmdPath, out _);

        Assert.True(service.IsAvailable);
        Assert.Equal(AgyProfileCmdPath, service.ExecutablePath);
        Assert.Null(service.AvailabilityBlocker);
    }

    [Fact]
    public async Task Availability_WhenUtilityMissing_AllOperationsFailClosedWithInstallLink()
    {
        var service = CreateService(executablePath: null, out var supervisor);

        Assert.False(service.IsAvailable);
        Assert.Null(service.ExecutablePath);
        Assert.Contains("https://github.com/haclongkim/agy-profile", service.AvailabilityBlocker);

        Assert.Null(await service.GetActiveProfileAsync());
        Assert.Empty(await service.ListProfilesAsync());

        var switchResult = await service.SwitchProfileAsync("work");
        Assert.False(switchResult.IsSwitched);
        Assert.Contains("https://github.com/haclongkim/agy-profile", switchResult.FailureReason);

        Assert.Empty(supervisor.Specifications);
    }

    [Fact]
    public async Task ListProfiles_ParsesDocumentedUtilityFormat_WithActiveMarker()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(standardOutput: ListOutput));

        var profiles = await service.ListProfilesAsync();

        Assert.Equal(2, profiles.Count);

        var active = profiles.Single(profile => profile.Name == "work");
        Assert.True(active.IsActive);

        var inactive = profiles.Single(profile => profile.Name == "personal");
        Assert.False(inactive.IsActive);
    }

    [Fact]
    public async Task ListProfiles_IgnoresHeadersWarningsAndBlankLines()
    {
        var output =
            "Saved profiles ('*' = matches the logged-in account):\n" +
            "\n" +
            "ПРЕДУПРЕЖДЕНИЕ: Not logged in to any account (credential 'gemini:antigravity' does not exist).\n" +
            "  work                 2026-09-22T10:00:00\n";

        var service = CreateService(AgyProfileCmdPath, out _, _ => CreateResult(standardOutput: output));

        var profiles = await service.ListProfilesAsync();

        var profile = Assert.Single(profiles);
        Assert.Equal("work", profile.Name);
        Assert.False(profile.IsActive);
    }

    [Fact]
    public async Task ListProfiles_WhenUtilityFails_ReturnsEmptyList()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(
                standardError: "ERROR: boom",
                exitCode: 1,
                terminationReason: ProcessTerminationReason.UnexpectedExit));

        Assert.Empty(await service.ListProfilesAsync());
    }

    [Fact]
    public async Task GetActiveProfile_ParsesCurrentCommandOutput()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(standardOutput: "Active profile: work\n"));

        Assert.Equal("work", await service.GetActiveProfileAsync());
    }

    [Fact]
    public async Task GetActiveProfile_WhenNotLoggedIn_ReturnsNull()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(
                standardOutput: "Not logged in (credential 'gemini:antigravity' does not exist).\n"));

        Assert.Null(await service.GetActiveProfileAsync());
    }

    [Fact]
    public async Task Commands_UseDocumentedListCurrentSwitchInvocations_WithoutForcedFlags()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out var supervisor,
            specification => specification.Arguments.Contains("list")
                ? CreateResult(standardOutput: ListOutput)
                : specification.Arguments.Contains("current")
                    ? CreateResult(standardOutput: "Active profile: work\n")
                    : CreateResult(standardOutput: "Switched to profile 'work' (user@example.com).\n"));

        await service.ListProfilesAsync();
        await service.GetActiveProfileAsync();
        var switchResult = await service.SwitchProfileAsync("work");

        Assert.True(switchResult.IsSwitched);
        Assert.Equal("work", switchResult.ActiveProfile);

        Assert.Equal(4, supervisor.Specifications.Count);

        var listSpecification = supervisor.Specifications[0];
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), listSpecification.FileName);
        Assert.Equal(new[] { "/d", "/c", AgyProfileCmdPath, "list" }, listSpecification.Arguments);

        var currentSpecification = supervisor.Specifications[1];
        Assert.Equal(new[] { "/d", "/c", AgyProfileCmdPath, "current" }, currentSpecification.Arguments);

        var switchSpecification = supervisor.Specifications[2];
        Assert.Equal(new[] { "/d", "/c", AgyProfileCmdPath, "switch", "work" }, switchSpecification.Arguments);

        // Confirmation re-reads the active profile via 'current'.
        var confirmationSpecification = supervisor.Specifications[3];
        Assert.Equal(new[] { "/d", "/c", AgyProfileCmdPath, "current" }, confirmationSpecification.Arguments);

        Assert.All(
            supervisor.Specifications.SelectMany(specification => specification.Arguments),
            argument =>
            {
                Assert.DoesNotContain("-Force", argument, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("next", argument, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("random", argument, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task Commands_RejectPowerShellLauncherBeforeInvocation_WhenOnlyPs1IsAvailable()
    {
        var service = CreateService(
            AgyProfilePs1Path,
            out var supervisor,
            _ => CreateResult(standardOutput: ""));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ListProfilesAsync());

        Assert.Equal("executablePath", error.ParamName);
        Assert.Empty(supervisor.Specifications);
    }

    [Fact]
    public async Task Switch_WhenAgyIsRunning_IsRefusedWithoutInvocation()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out var supervisor,
            _ => CreateResult(standardOutput: "Active profile: work\n"),
            agyRunning: true);

        var result = await service.SwitchProfileAsync("work");

        Assert.False(result.IsSwitched);
        Assert.Contains("agy process is currently running", result.FailureReason);
        Assert.Empty(supervisor.Specifications);
    }

    [Theory]
    [InlineData("work; echo pwned")]
    [InlineData("work with spaces")]
    [InlineData("work/../escape")]
    [InlineData("профиль")]
    public async Task Switch_WhenProfileNameIsInvalid_IsRefusedWithoutInvocation(string profileName)
    {
        var service = CreateService(AgyProfileCmdPath, out var supervisor, _ => CreateResult());

        var result = await service.SwitchProfileAsync(profileName);

        Assert.False(result.IsSwitched);
        Assert.Contains("is invalid", result.FailureReason);
        Assert.Empty(supervisor.Specifications);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("random")]
    [InlineData("NEXT")]
    public async Task Switch_WhenRotationCommandIsRequested_IsRefusedWithoutInvocation(string profileName)
    {
        var service = CreateService(AgyProfileCmdPath, out var supervisor, _ => CreateResult());

        var result = await service.SwitchProfileAsync(profileName);

        Assert.False(result.IsSwitched);
        Assert.Contains("forbidden", result.FailureReason);
        Assert.Empty(supervisor.Specifications);
    }

    [Theory]
    [InlineData("-Force")]
    [InlineData("-force")]
    [InlineData("--help")]
    [InlineData("-work")]
    public async Task Switch_OptionShapedProfileNameNeverReachesUtility(string profileName)
    {
        var service = CreateService(AgyProfileCmdPath, out var supervisor,
            _ => CreateResult(standardOutput: $"Active profile: {profileName}\n"));

        var result = await service.SwitchProfileAsync(profileName);

        Assert.False(result.IsSwitched);
        Assert.Empty(supervisor.Specifications);
    }

    [Fact]
    public async Task Switch_WhenUtilityReportsError_ReturnsFailureWithSanitizedDetail()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(
                standardError: "ERROR: Profile 'missing' does not exist. See: agy-profile list",
                exitCode: 1,
                terminationReason: ProcessTerminationReason.UnexpectedExit));

        var result = await service.SwitchProfileAsync("missing");

        Assert.False(result.IsSwitched);
        Assert.Contains("ERROR: Profile 'missing' does not exist", result.FailureReason);
    }

    [Fact]
    public async Task Switch_WhenConfirmationDiffers_ReturnsFailure()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            specification => specification.Arguments.Contains("switch")
                ? CreateResult(standardOutput: "Switched to profile 'work' (user@example.com).\n")
                : CreateResult(standardOutput: "Active profile: personal\n"));

        var result = await service.SwitchProfileAsync("work");

        Assert.False(result.IsSwitched);
        Assert.Contains("not confirmed", result.FailureReason);
        Assert.Contains("personal", result.FailureReason);
    }

    [Fact]
    public async Task Switch_ConfirmationIsCaseInsensitive()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            specification => specification.Arguments.Contains("switch")
                ? CreateResult(standardOutput: "Switched to profile 'Work' (user@example.com).\n")
                : CreateResult(standardOutput: "Active profile: work\n"));

        var result = await service.SwitchProfileAsync("Work");

        Assert.True(result.IsSwitched);
        Assert.Equal("work", result.ActiveProfile);
    }

    [Fact]
    public async Task Switch_WhenUtilityTimesOut_ReturnsFailure()
    {
        var service = CreateService(
            AgyProfileCmdPath,
            out _,
            _ => CreateResult(),
            commandTimeout: TimeSpan.FromMilliseconds(50),
            hangForever: true);

        var result = await service.SwitchProfileAsync("work");

        Assert.False(result.IsSwitched);
        Assert.Contains("timed out", result.FailureReason);
    }

    [Fact]
    public async Task Switch_WhenProfileNameIsEmpty_Throws()
    {
        var service = CreateService(AgyProfileCmdPath, out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SwitchProfileAsync(" "));
    }

    [Fact]
    public async Task ListProfiles_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-agy-profile";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var service = CreateService(AgyProfileCmdPath, out var supervisor, _ => CreateResult(standardOutput: ListOutput));

            await service.ListProfilesAsync();

            var specification = Assert.Single(supervisor.Specifications);
            Assert.False(specification.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(AgyProfileCmdPath);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Path.GetDirectoryName(AgyProfileCmdPath)!,
                specification.EnvironmentVariables["PATH"],
                StringComparison.OrdinalIgnoreCase);
            AssertProfileLocations(specification);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    private static void AssertProfileLocations(ProcessStartSpecification specification)
    {
        foreach (var name in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                Assert.False(specification.EnvironmentVariables.ContainsKey(name));
            else
                Assert.Equal(value, specification.EnvironmentVariables[name]);
        }
    }

    private static AgyProfileService CreateService(
        string? executablePath,
        out ScriptedProcessSupervisor supervisor,
        Func<ProcessStartSpecification, ProcessExecutionResult>? responder = null,
        bool agyRunning = false,
        TimeSpan? commandTimeout = null,
        bool hangForever = false)
    {
        supervisor = new ScriptedProcessSupervisor(
            responder ?? (_ => CreateResult()),
            hangForever ? Timeout.InfiniteTimeSpan : null);

        return new AgyProfileService(
            new StubExecutableResolver(executablePath),
            new StubProcessInspector(agyRunning),
            supervisor,
            commandTimeout);
    }

    private static ProcessExecutionResult CreateResult(
        string standardOutput = "",
        string standardError = "",
        int exitCode = 0,
        ProcessTerminationReason terminationReason = ProcessTerminationReason.None,
        string? failureMessage = null)
    {
        return new ProcessExecutionResult
        {
            ExecutionId = "agy-profile-test",
            ProcessId = 4242,
            TerminationReason = terminationReason,
            ExitCode = exitCode,
            StartedAtUtc = StartedAt,
            ExitedAtUtc = StartedAt.AddSeconds(1),
            RunDirectory = @"C:\runs\agy-profile-test",
            StandardOutputLogPath = @"C:\runs\agy-profile-test\stdout.log",
            StandardErrorLogPath = @"C:\runs\agy-profile-test\stderr.log",
            StandardOutputBytes = standardOutput.Length,
            StandardErrorBytes = standardError.Length,
            StandardOutputHead = standardOutput,
            StandardOutputTail = standardOutput,
            StandardErrorHead = standardError,
            StandardErrorTail = standardError,
            OutputOverflowed = false,
            FailureMessage = failureMessage
        };
    }

    private sealed class StubExecutableResolver : IAgyProfileExecutableResolver
    {
        private readonly string? _executablePath;

        public StubExecutableResolver(string? executablePath)
        {
            _executablePath = executablePath;
        }

        public AgyProfileExecutableResolution Resolve() =>
            _executablePath is null
                ? AgyProfileExecutableResolution.NotFound()
                : AgyProfileExecutableResolution.Found(_executablePath);
    }

    private sealed class StubProcessInspector : IAgyProcessInspector
    {
        private readonly bool _agyRunning;

        public StubProcessInspector(bool agyRunning)
        {
            _agyRunning = agyRunning;
        }

        public Task<bool> IsAgyRunningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_agyRunning);
    }

    private sealed class ScriptedProcessSupervisor : IProcessSupervisor
    {
        private readonly Func<ProcessStartSpecification, ProcessExecutionResult> _responder;
        private readonly TimeSpan? _hangDuration;

        public ScriptedProcessSupervisor(
            Func<ProcessStartSpecification, ProcessExecutionResult> responder,
            TimeSpan? hangDuration)
        {
            _responder = responder;
            _hangDuration = hangDuration;
        }

        public List<ProcessStartSpecification> Specifications { get; } = new();

        public async Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            Specifications.Add(specification);

            if (_hangDuration is not null)
            {
                await Task.Delay(_hangDuration.Value, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return _responder(specification);
        }
    }
}
