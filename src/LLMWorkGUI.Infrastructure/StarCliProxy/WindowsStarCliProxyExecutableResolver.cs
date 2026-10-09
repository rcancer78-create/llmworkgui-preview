using LLMWorkGUI.Application.StarCliProxy;

namespace LLMWorkGUI.Infrastructure.StarCliProxy;

/// <summary>
/// Locates the star-cliproxy gateway on the current Windows user account: environment override
/// (<c>STAR_CLIPROXY_EXECUTABLE</c> / <c>STAR_CLIPROXY_PATH</c>), then PATH, then
/// <c>%LOCALAPPDATA%\star-cliproxy</c> (ТЗ §6.11a). No credential or configuration file is read
/// during discovery; a missing gateway keeps Codex/AGY routes in degraded mode with an exact blocker.
/// </summary>
public sealed class WindowsStarCliProxyExecutableResolver : IStarCliProxyExecutableResolver
{
    public const string ExecutableName = "star-cliproxy";

    public const string ExecutablePathVariable = "STAR_CLIPROXY_EXECUTABLE";

    public const string PathPathVariable = "STAR_CLIPROXY_PATH";

    public const string InstallDirectoryName = "star-cliproxy";

    private const string LocalAppDataVariable = "LOCALAPPDATA";

    private const string PathVariable = "PATH";

    private static readonly string[] PathExtensions = [".exe", ".cmd", ".ps1", ".bat"];

    private readonly Func<string, string?> _environmentProvider;
    private readonly Func<string, bool> _fileExists;

    public WindowsStarCliProxyExecutableResolver()
        : this(Environment.GetEnvironmentVariable, File.Exists)
    {
    }

    public WindowsStarCliProxyExecutableResolver(
        Func<string, string?> environmentProvider,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(fileExists);

        _environmentProvider = environmentProvider;
        _fileExists = fileExists;
    }

    public StarCliProxyExecutableResolution Resolve()
    {
        var explicitPath = _environmentProvider(ExecutablePathVariable)
            ?? _environmentProvider(PathPathVariable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var trimmed = explicitPath.Trim().Trim('"');
            var resolved = TryResolveFile(trimmed);

            return resolved is not null
                ? StarCliProxyExecutableResolution.Found(resolved)
                : StarCliProxyExecutableResolution.NotFound(
                    $"The star-cliproxy executable configured through the environment ('{trimmed}') does not exist. " +
                    StarCliProxyPolicy.NotInstalledBlocker);
        }

        var fromPath = ResolveFromPath(_environmentProvider(PathVariable));
        if (fromPath is not null)
        {
            return StarCliProxyExecutableResolution.Found(fromPath);
        }

        var localAppData = _environmentProvider(LocalAppDataVariable);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installDirectory = Path.Combine(localAppData, InstallDirectoryName);

            foreach (var extension in PathExtensions)
            {
                var candidate = TryResolveFile(Path.Combine(installDirectory, ExecutableName + extension));
                if (candidate is not null)
                {
                    return StarCliProxyExecutableResolution.Found(candidate);
                }
            }
        }

        return StarCliProxyExecutableResolution.NotFound();
    }

    private string? ResolveFromPath(string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var rawEntry in pathVariable.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawEntry.Trim().Trim('"').Trim();
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in PathExtensions)
            {
                var candidate = TryResolveFile(Path.Combine(directory, ExecutableName + extension));
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? TryResolveFile(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return _fileExists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
