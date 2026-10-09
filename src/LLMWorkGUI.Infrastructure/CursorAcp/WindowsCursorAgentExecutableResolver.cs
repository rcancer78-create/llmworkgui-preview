using System.Globalization;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Locates the Cursor Agent CLI on the current Windows user account: environment override
/// (<c>CURSOR_AGENT_EXECUTABLE</c> / <c>CURSOR_AGENT_PATH</c>), then PATH, then the standard install
/// locations (ТЗ §6.2, §6.12). The version is probed through the shared process supervisor with a
/// bounded timeout and masked diagnostics. An absent or unusable CLI produces a typed
/// <see cref="CursorAcpExecutableResolution"/> instead of an exception, so the backend degrades cleanly.
/// </summary>
public sealed partial class WindowsCursorAgentExecutableResolver : ICursorExecutableResolver
{
    public const string ExecutablePathVariable = "CURSOR_AGENT_EXECUTABLE";

    public const string PathPathVariable = "CURSOR_AGENT_PATH";

    private const string PathVariable = "PATH";
    private const string LocalAppDataVariable = "LOCALAPPDATA";
    private const string AppDataVariable = "APPDATA";
    private const string UserProfileVariable = "USERPROFILE";

    private static readonly string[] ExecutableExtensions = [".exe", ".cmd", ".bat"];

    private readonly IProcessSupervisor _processSupervisor;
    private readonly CursorAcpOptions _options;
    private readonly Func<string, string?> _environmentProvider;
    private readonly Func<string, bool> _fileExists;
    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly SensitiveDataFilter? _filter;
    private readonly string? _userProfile;
    private readonly ILogger<WindowsCursorAgentExecutableResolver> _logger;

    public WindowsCursorAgentExecutableResolver(
        IProcessSupervisor processSupervisor,
        IOptions<CursorAcpOptions>? options = null,
        SensitiveDataFilter? filter = null,
        ILogger<WindowsCursorAgentExecutableResolver>? logger = null)
        : this(
            processSupervisor,
            Environment.GetEnvironmentVariable,
            File.Exists,
            searchDirectories: null,
            options?.Value,
            filter,
            logger)
    {
    }

    public WindowsCursorAgentExecutableResolver(
        IProcessSupervisor processSupervisor,
        Func<string, string?> environmentProvider,
        Func<string, bool> fileExists,
        IReadOnlyList<string>? searchDirectories = null,
        CursorAcpOptions? options = null,
        SensitiveDataFilter? filter = null,
        ILogger<WindowsCursorAgentExecutableResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(fileExists);

        _processSupervisor = processSupervisor;
        _environmentProvider = environmentProvider;
        _fileExists = fileExists;
        _options = options ?? new CursorAcpOptions();
        _options.Validate();
        _filter = filter;
        _userProfile = environmentProvider(UserProfileVariable);
        _searchDirectories = searchDirectories ?? BuildSearchDirectories();
        _logger = logger ?? NullLogger<WindowsCursorAgentExecutableResolver>.Instance;
    }

    public async Task<CursorAcpExecutableResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var explicitPath = _environmentProvider(ExecutablePathVariable)
                ?? _environmentProvider(PathPathVariable);

            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var configured = TryResolveConfiguredPath(explicitPath);

                if (configured is null)
                {
                    return CursorAcpExecutableResolution.Missing(
                        $"The configured Cursor Agent executable '{Mask(explicitPath.Trim().Trim('"'))}' does not exist. " +
                        CursorAcpPolicy.NotInstalledBlocker);
                }

                return await ProbeVersionAsync(configured, cancellationToken).ConfigureAwait(false);
            }

            var located = LocateInSearchDirectories();

            if (located is null)
            {
                return CursorAcpExecutableResolution.Missing();
            }

            return await ProbeVersionAsync(located, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Cursor Agent CLI discovery failed unexpectedly.");

            return CursorAcpExecutableResolution.Unresolved(
                $"Cursor Agent CLI discovery failed: {Mask(exception.Message)}");
        }
    }

    private async Task<CursorAcpExecutableResolution> ProbeVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var executionId = "cursor-agent-version-" + Guid.NewGuid().ToString("N");
        var (fileName, arguments) = CursorAcpCommandLine.Create(
            executablePath,
            [CursorAcpCommandLine.VersionArgument]);

        var specification = new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = fileName,
            Arguments = arguments,
            InheritEnvironment = false,
            EnvironmentVariables = ProcessRuntimeEnvironment.CreateBaseline(executablePath),
            StdinPolicy = ProcessStdinPolicy.Closed
        };

        ProcessExecutionResult result;

        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(_options.VersionProbeTimeout);

            try
            {
                result = await _processSupervisor
                    .ExecuteAsync(specification, cancellationToken: timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return CursorAcpExecutableResolution.Unresolved(
                    $"'cursor-agent --version' did not complete within {_options.VersionProbeTimeout} " +
                    $"for '{Mask(executablePath)}'.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CursorAcpExecutableResolution.Unresolved(
                    $"Failed to execute 'cursor-agent --version' for '{Mask(executablePath)}': {Mask(exception.Message)}");
            }

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return CursorAcpExecutableResolution.Unresolved(
                    $"'cursor-agent --version' did not complete within {_options.VersionProbeTimeout} " +
                    $"for '{Mask(executablePath)}'.");
            }
        }

        if (result.ExitCode is not 0)
        {
            var exitCode = result.ExitCode is { } code
                ? code.ToString(CultureInfo.InvariantCulture)
                : "unknown";

            var detail = FirstNonEmptyLine(result.StandardErrorHead)
                ?? FirstNonEmptyLine(result.StandardErrorTail);

            var suffix = detail is null ? string.Empty : $" stderr: {Mask(detail)}";

            return CursorAcpExecutableResolution.Unresolved(
                $"'cursor-agent --version' exited with code {exitCode} for '{Mask(executablePath)}'.{suffix}");
        }

        var output = string.Concat(
            result.StandardOutputHead,
            Environment.NewLine,
            result.StandardOutputTail);

        if (!TryParseVersion(output, out var version))
        {
            return CursorAcpExecutableResolution.Unresolved(
                $"'cursor-agent --version' did not report a parseable version for '{Mask(executablePath)}'.");
        }

        return CursorAcpExecutableResolution.Found(executablePath, version);
    }

    private string? LocateInSearchDirectories()
    {
        foreach (var directory in _searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in ExecutableExtensions)
            {
                var candidate = TryCombine(directory.Trim().Trim('"'), CursorAcpPolicy.ExecutableName + extension);

                if (candidate is not null && TryFileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? TryResolveConfiguredPath(string explicitPath)
    {
        var trimmed = explicitPath.Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (TryFileExists(trimmed))
        {
            return trimmed;
        }

        if (ExecutableExtensions.Contains(Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var extension in ExecutableExtensions)
        {
            var candidate = TryCombine(trimmed, CursorAcpPolicy.ExecutableName + extension);

            if (candidate is not null && TryFileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> BuildSearchDirectories()
    {
        var directories = new List<string>();

        AddPathEntries(directories, _environmentProvider(PathVariable));
        AddCombinedDirectory(directories, _environmentProvider(LocalAppDataVariable), "Programs", "cursor", "resources", "app", "bin");
        AddCombinedDirectory(directories, _environmentProvider(LocalAppDataVariable), "Programs", "cursor");
        AddCombinedDirectory(directories, _environmentProvider(LocalAppDataVariable), "Programs", "cursor-agent");
        AddCombinedDirectory(directories, _environmentProvider(LocalAppDataVariable), "cursor-agent");
        AddCombinedDirectory(directories, _environmentProvider(AppDataVariable), "npm");
        AddCombinedDirectory(directories, _environmentProvider(UserProfileVariable), ".cursor", "bin");

        return directories;
    }

    private static void AddPathEntries(List<string> directories, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return;
        }

        foreach (var rawEntry in pathVariable.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawEntry.Trim().Trim('"').Trim();

            if (directory.Length > 0)
            {
                directories.Add(directory);
            }
        }
    }

    private static void AddCombinedDirectory(List<string> directories, string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            directories.Add(Path.Combine([root, .. parts]));
        }
        catch (ArgumentException)
        {
        }
    }

    private static string? TryCombine(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool TryFileExists(string candidate)
    {
        try
        {
            return _fileExists(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryParseVersion(string? output, out string version)
    {
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = VersionPattern().Match(output);

        if (!match.Success)
        {
            return false;
        }

        version = match.Groups["version"].Value;
        return true;
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    private string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var masked = _filter?.Redact(value) ?? value;

        if (!string.IsNullOrWhiteSpace(_userProfile))
        {
            masked = masked.Replace(_userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return UserProfilePathPattern().Replace(masked, "%USERPROFILE%");
    }

    [GeneratedRegex(
        @"(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(
        @"[A-Za-z]:\\Users\\[^\\\s""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathPattern();
}
