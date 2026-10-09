using LLMWorkGUI.Application.Cli;

namespace LLMWorkGUI.Infrastructure.Cli;

public sealed class PathCliExecutableLocator : ICliExecutableLocator
{
    private static readonly string[] FallbackExtensions = { ".exe", ".cmd", ".bat", ".com", ".ps1" };

    public Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        cancellationToken.ThrowIfCancellationRequested();

        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return Task.FromResult<string?>(null);
        }

        var searchDirectories = pathVariable.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = ResolveExtensions(executableName);

        foreach (var directory in searchDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateDirectory = directory.Trim('"');

            foreach (var extension in extensions)
            {
                var candidatePath = TryCombine(candidateDirectory, executableName + extension);

                if (candidatePath is null)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(candidatePath))
                    {
                        return Task.FromResult<string?>(candidatePath);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                }
            }
        }

        return Task.FromResult<string?>(null);
    }

    private static string[] ResolveExtensions(string executableName)
    {
        if (Path.HasExtension(executableName))
        {
            return new[] { string.Empty };
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");

        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return FallbackExtensions;
        }

        var extensions = pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return extensions.Length == 0 ? FallbackExtensions : extensions;
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
