using System.Collections.ObjectModel;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

/// <summary>One explicit owned server launch; neither account identity nor SQL release authority.</summary>
public sealed class OpenCodeOwnedServerLaunch
{
    public OpenCodeOwnedServerLaunch(string executionId, string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (string.IsNullOrWhiteSpace(executionId) || executionId.Length > 128
            || executionId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("An owned execution identity is required.", nameof(executionId));
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("An existing absolute owned working directory is required.", nameof(workingDirectory));
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environmentVariables)
        {
            if (string.IsNullOrEmpty(key) || key.Any(c => char.IsControl(c) || c == '=')
                || value is null || value.Contains('\0') || !environment.TryAdd(key, value))
                throw new ArgumentException("Invalid or duplicate child environment entry.", nameof(environmentVariables));
        }
        ExecutionId = executionId;
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        EnvironmentVariables = new ReadOnlyDictionary<string, string>(environment);
    }

    public string ExecutionId { get; }
    public string WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
}
