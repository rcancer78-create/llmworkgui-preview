using LLMWorkGUI.Application.Agy;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Locates the agy-profile utility on the current Windows user account: first on PATH,
/// then in <c>%LOCALAPPDATA%\agy-profile\agy-profile.cmd</c> and
/// <c>%LOCALAPPDATA%\agy-profile\agy-profile.ps1</c> (ТЗ §6.11a).
/// </summary>
public sealed class WindowsAgyProfileExecutableResolver : IAgyProfileExecutableResolver
{
    public const string UtilityName = "agy-profile";

    public const string InstallDirectoryName = "agy-profile";

    private static readonly string[] PathExtensions = [".cmd", ".ps1", ".exe", ".bat"];

    private readonly Func<string?> _localAppDataProvider;
    private readonly Func<string?> _pathProvider;
    private readonly Func<string, bool> _fileExists;

    public WindowsAgyProfileExecutableResolver()
        : this(
            () => Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists)
    {
    }

    public WindowsAgyProfileExecutableResolver(
        Func<string?> localAppDataProvider,
        Func<string?> pathProvider,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(localAppDataProvider);
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentNullException.ThrowIfNull(fileExists);

        _localAppDataProvider = localAppDataProvider;
        _pathProvider = pathProvider;
        _fileExists = fileExists;
    }

    public AgyProfileExecutableResolution Resolve()
    {
        var onPath = ResolveFromPath(_pathProvider());
        if (onPath is not null)
        {
            return AgyProfileExecutableResolution.Found(onPath);
        }

        var localAppData = _localAppDataProvider();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installDirectory = Path.Combine(localAppData, InstallDirectoryName);

            var commandScript = TryResolveFile(Path.Combine(installDirectory, "agy-profile.cmd"));
            if (commandScript is not null)
            {
                return AgyProfileExecutableResolution.Found(commandScript);
            }

            var powerShellScript = TryResolveFile(Path.Combine(installDirectory, "agy-profile.ps1"));
            if (powerShellScript is not null)
            {
                return AgyProfileExecutableResolution.Found(powerShellScript);
            }
        }

        return AgyProfileExecutableResolution.NotFound();
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
                var candidate = TryResolveFile(Path.Combine(directory, UtilityName + extension));
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
