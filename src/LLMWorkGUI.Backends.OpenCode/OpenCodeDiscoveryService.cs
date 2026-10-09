using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed class OpenCodeDiscoveryService : IOpenCodeDiscoveryService
{
    public const string ExecutableName = "opencode";

    private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat" };

    private readonly IProcessSupervisor _processSupervisor;
    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly TimeSpan _probeTimeout;
    private readonly ILogger<OpenCodeDiscoveryService> _logger;

    public OpenCodeDiscoveryService(
        IProcessSupervisor processSupervisor,
        IReadOnlyList<string>? searchDirectories = null,
        TimeSpan? probeTimeout = null,
        ILogger<OpenCodeDiscoveryService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);

        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(15);

        if (_probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout),
                _probeTimeout,
                "Probe timeout must be positive.");
        }

        _processSupervisor = processSupervisor;
        _searchDirectories = searchDirectories ?? BuildDefaultSearchDirectories();
        _logger = logger ?? NullLogger<OpenCodeDiscoveryService>.Instance;
    }

    public async Task<OpenCodeDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var executablePath = LocateExecutable();

            if (executablePath is null)
            {
                return OpenCodeDiscoveryResult.NotInstalled(
                    $"OpenCode CLI executable '{ExecutableName}' was not found on PATH or in the standard install directories.");
            }

            return await ProbeVersionAsync(executablePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "OpenCode CLI discovery failed unexpectedly.");

            return OpenCodeDiscoveryResult.NotInstalled($"OpenCode CLI discovery failed: {exception.Message}");
        }
    }

    private string? LocateExecutable()
    {
        foreach (var directory in _searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in ExecutableExtensions)
            {
                var candidate = TryCombine(directory.Trim().Trim('"'), ExecutableName + extension);

                if (candidate is not null && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<OpenCodeDiscoveryResult> ProbeVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var executionId = "opencode-discovery-" + Guid.NewGuid().ToString("N");
        var (fileName, arguments) = OpenCodeCommandLine.Create(executablePath, new[] { "--version" });
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
            timeoutCts.CancelAfter(_probeTimeout);

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
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return OpenCodeDiscoveryResult.Unsupported(
                    executablePath,
                    version: null,
                    $"'{ExecutableName} --version' did not complete within {_probeTimeout}.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return OpenCodeDiscoveryResult.Unsupported(
                    executablePath,
                    version: null,
                    $"Failed to execute '{ExecutableName} --version': {exception.Message}");
            }

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return OpenCodeDiscoveryResult.Unsupported(
                    executablePath,
                    version: null,
                    $"'{ExecutableName} --version' did not complete within {_probeTimeout}.");
            }
        }

        var output = string.Concat(
            result.StandardOutputHead,
            Environment.NewLine,
            result.StandardOutputTail,
            Environment.NewLine,
            result.StandardErrorHead,
            Environment.NewLine,
            result.StandardErrorTail);

        if (result.ExitCode is not 0)
        {
            var exitCode = result.ExitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";

            return OpenCodeDiscoveryResult.Unsupported(
                executablePath,
                version: null,
                $"'{ExecutableName} --version' exited with code {exitCode}.");
        }

        if (!OpenCodeVersionParser.TryParse(output, out var version))
        {
            return OpenCodeDiscoveryResult.Unsupported(
                executablePath,
                version: null,
                $"'{ExecutableName} --version' did not report a parseable version.");
        }

        if (!OpenCodeVersionParser.IsSupported(version))
        {
            return OpenCodeDiscoveryResult.Unsupported(
                executablePath,
                version.ToString(),
                $"OpenCode version {version} is below the minimum supported version " +
                $"{OpenCodeVersionBaseline.MinimumSupportedVersion}.");
        }

        return OpenCodeDiscoveryResult.Detected(executablePath, version.ToString());
    }

    private static IReadOnlyList<string> BuildDefaultSearchDirectories()
    {
        var directories = new List<string>();
        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            directories.AddRange(
                pathVariable.Split(
                    Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "opencode"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".opencode",
            "bin"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "opencode"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "opencode"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Links"));

        return directories;
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
}
