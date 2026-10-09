using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Native agy-profile integration using only the documented <c>list</c>, <c>current</c> and
/// <c>switch</c> commands (ТЗ §6.4, §6.11a). The service never passes <c>-Force</c>, never
/// issues <c>next</c>/<c>random</c>, never reads or copies DPAPI credential files and never
/// logs credential material; only profile names and sanitized command diagnostics are used.
/// </summary>
public sealed class AgyProfileService : IAgyProfileService
{
    public const string ListCommand = "list";

    public const string CurrentCommand = "current";

    public const string SwitchCommand = "switch";

    public const string ActiveProfilePrefix = "Active profile:";

    public const string SavedProfilesHeaderPrefix = "Saved profiles";

    /// <summary>Bounded timeout for a single agy-profile utility invocation.</summary>
    public static readonly TimeSpan ProfileCommandTimeout = TimeSpan.FromMinutes(1);

    private const int MaxErrorExcerptLength = 400;

    private readonly IAgyProcessInspector _processInspector;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly ILogger<AgyProfileService> _logger;
    private readonly AgyProfileExecutableResolution _resolution;
    private readonly TimeSpan _commandTimeout;

    public AgyProfileService(
        IAgyProfileExecutableResolver executableResolver,
        IAgyProcessInspector processInspector,
        IProcessSupervisor processSupervisor,
        TimeSpan? commandTimeout = null,
        ILogger<AgyProfileService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(executableResolver);
        ArgumentNullException.ThrowIfNull(processInspector);
        ArgumentNullException.ThrowIfNull(processSupervisor);

        _processInspector = processInspector;
        _processSupervisor = processSupervisor;
        _logger = logger ?? NullLogger<AgyProfileService>.Instance;
        _resolution = executableResolver.Resolve();
        _commandTimeout = commandTimeout ?? ProfileCommandTimeout;
    }

    public bool IsAvailable => _resolution.IsAvailable;

    public string? ExecutablePath => _resolution.ExecutablePath;

    public string? AvailabilityBlocker =>
        _resolution.IsAvailable ? null : AgyProfilePolicy.NotInstalledBlocker;

    public async Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var execution = await RunUtilityAsync(CurrentCommand, profileName: null, cancellationToken)
            .ConfigureAwait(false);

        return execution.IsSuccess ? ParseActiveProfile(execution.StandardOutput) : null;
    }

    public async Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Array.Empty<AgyProfileSummary>();
        }

        var execution = await RunUtilityAsync(ListCommand, profileName: null, cancellationToken)
            .ConfigureAwait(false);

        return execution.IsSuccess
            ? ParseProfiles(execution.StandardOutput)
            : Array.Empty<AgyProfileSummary>();
    }

    public async Task<AgyProfileSwitchResult> SwitchProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        if (!IsAvailable)
        {
            return AgyProfileSwitchResult.Failure(AvailabilityBlocker!);
        }

        if (!IsValidProfileName(profileName))
        {
            return AgyProfileSwitchResult.Failure(
                $"AGY profile name '{profileName}' is invalid: only letters, digits, '-' and '_' are allowed.");
        }

        if (IsForbiddenRotationCommand(profileName))
        {
            return AgyProfileSwitchResult.Failure(
                $"Automatic rotation command '{profileName}' is forbidden by policy (ТЗ §6.11a); " +
                "only explicit user-selected profile switches are permitted.");
        }

        var agyRunning = await _processInspector
            .IsAgyRunningAsync(cancellationToken)
            .ConfigureAwait(false);

        if (agyRunning)
        {
            return AgyProfileSwitchResult.Failure(
                "An agy process is currently running; switching the AGY profile is refused until " +
                "all AGY executions finish (ТЗ §6.4, §6.11a).");
        }

        var execution = await RunUtilityAsync(SwitchCommand, profileName, cancellationToken)
            .ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            return AgyProfileSwitchResult.Failure(
                execution.FailureReason ?? $"agy-profile {SwitchCommand} '{profileName}' failed.");
        }

        var confirmedProfile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(confirmedProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return AgyProfileSwitchResult.Failure(
                $"AGY profile switch was not confirmed: requested '{profileName}', " +
                $"active profile is '{confirmedProfile ?? "(none)"}'.");
        }

        return AgyProfileSwitchResult.Success(confirmedProfile!);
    }

    private async Task<UtilityExecution> RunUtilityAsync(
        string command,
        string? profileName,
        CancellationToken cancellationToken)
    {
        var specification = BuildSpecification(command, profileName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_commandTimeout);

        ProcessExecutionResult result;
        try
        {
            result = await _processSupervisor
                .ExecuteAsync(specification, outputProgress: null, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "agy-profile {Command} timed out after {Timeout}.",
                command,
                _commandTimeout);

            return new UtilityExecution(
                IsSuccess: false,
                StandardOutput: string.Empty,
                FailureReason: $"agy-profile {command} timed out after {_commandTimeout}.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (result.TerminationReason == ProcessTerminationReason.None && result.ExitCode == 0)
        {
            return new UtilityExecution(
                IsSuccess: true,
                StandardOutput: result.StandardOutputHead,
                FailureReason: null);
        }

        var failureReason = BuildCommandFailureReason(command, result);

        _logger.LogWarning(
            "agy-profile {Command} failed (termination {Termination}, exit code {ExitCode}).",
            command,
            result.TerminationReason,
            result.ExitCode);

        return new UtilityExecution(
            IsSuccess: false,
            StandardOutput: result.StandardOutputHead,
            FailureReason: failureReason);
    }

    private ProcessStartSpecification BuildSpecification(string command, string? profileName)
    {
        var utilityArguments = new List<string> { command };
        if (profileName is not null)
        {
            utilityArguments.Add(profileName);
        }

        var (fileName, arguments) = BuildLaunchCommand(_resolution.ExecutablePath!, utilityArguments);

        return new ProcessStartSpecification
        {
            ExecutionId = $"agy-profile-{command}-{Guid.NewGuid():N}",
            FileName = fileName,
            Arguments = arguments,
            InheritEnvironment = false,
            EnvironmentVariables = AgyProcessEnvironment.Create(_resolution.ExecutablePath!),
            StdinPolicy = ProcessStdinPolicy.Closed
        };
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildLaunchCommand(
        string executablePath,
        IReadOnlyList<string> utilityArguments)
    {
        var extension = Path.GetExtension(executablePath);
        InterpreterLaunchGuard.RejectInterpreterEntry(executablePath, utilityArguments);

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var commandInterpreterArguments = new List<string> { "/d", "/c", executablePath };
            commandInterpreterArguments.AddRange(utilityArguments);

            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), commandInterpreterArguments);
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powerShellArguments = new List<string>
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                executablePath
            };
            powerShellArguments.AddRange(utilityArguments);

            return (
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                powerShellArguments);
        }

        return (executablePath, utilityArguments);
    }

    internal static string? ParseActiveProfile(string standardOutput)
    {
        foreach (var line in EnumerateLines(standardOutput))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(ActiveProfilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = trimmed[ActiveProfilePrefix.Length..].Trim();

            return IsValidProfileName(name) ? name : null;
        }

        return null;
    }

    internal static IReadOnlyList<AgyProfileSummary> ParseProfiles(string standardOutput)
    {
        var profiles = new List<AgyProfileSummary>();

        foreach (var rawLine in EnumerateLines(standardOutput))
        {
            // Profile rows are indented with two spaces; headers and warnings are not.
            if (!rawLine.StartsWith("  ", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = rawLine.TrimStart();
            var isActive = trimmed.StartsWith('*');

            var content = isActive ? trimmed[1..].Trim() : trimmed.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var name = content.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!IsValidProfileName(name))
            {
                continue;
            }

            if (profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            profiles.Add(new AgyProfileSummary(name, isActive));
        }

        return profiles;
    }

    internal static bool IsValidProfileName(string profileName) =>
        AgyProfilePolicy.IsValidProfileName(profileName);

    internal static bool IsForbiddenRotationCommand(string profileName) =>
        AgyProfilePolicy.ForbiddenRotationCommands.Contains(profileName, StringComparer.OrdinalIgnoreCase);

    private static string BuildCommandFailureReason(string command, ProcessExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.FailureMessage))
        {
            return $"agy-profile {command} failed to start: {Sanitize(result.FailureMessage)}";
        }

        var errorLine = FindErrorLine(result.StandardErrorHead) ?? FindErrorLine(result.StandardOutputHead);

        var detail = errorLine is not null
            ? Sanitize(errorLine)
            : $"termination {result.TerminationReason}, exit code {result.ExitCode?.ToString() ?? "(none)"}";

        return $"agy-profile {command} failed: {detail}";
    }

    private static string? FindErrorLine(string text)
    {
        foreach (var line in EnumerateLines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string Sanitize(string message) =>
        message.Length <= MaxErrorExcerptLength ? message : message[..MaxErrorExcerptLength] + "...";

    private static IEnumerable<string> EnumerateLines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r'));

    private sealed record UtilityExecution(
        bool IsSuccess,
        string StandardOutput,
        string? FailureReason);
}
