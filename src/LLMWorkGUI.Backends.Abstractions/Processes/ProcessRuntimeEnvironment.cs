namespace LLMWorkGUI.Backends.Abstractions.Processes;

/// <summary>
/// The variables a managed Windows process needs to start. Callers add their own
/// values; nothing is copied from the parent environment.
/// </summary>
public static class ProcessRuntimeEnvironment
{
    public static Dictionary<string, string> CreateBaseline(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var systemRoot = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, ".."));
        var powerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0");
        var executableDirectory = Path.GetDirectoryName(executablePath);
        var path = string.IsNullOrEmpty(executableDirectory)
            ? Environment.SystemDirectory + Path.PathSeparator + powerShell
            : Environment.SystemDirectory + Path.PathSeparator + powerShell + Path.PathSeparator + executableDirectory;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["windir"] = systemRoot,
            ["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["PATH"] = path
        };
    }
}
