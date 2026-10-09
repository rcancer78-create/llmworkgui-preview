using LLMWorkGUI.Backends.Abstractions.Processes;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Builds the typed argv for launching <c>cursor-agent</c> (ТЗ §6.8): no shell interpolation, no
/// command-line string concatenation. Batch entry points are launched through
/// <c>cmd.exe /d /c &lt;script&gt; &lt;args...&gt;</c> with every argument passed as a separate token.
/// </summary>
public static class CursorAcpCommandLine
{
    public const string VersionArgument = "--version";

    public static (string FileName, IReadOnlyList<string> Arguments) Create(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (IsBatchFile(executablePath))
        {
            InterpreterLaunchGuard.RejectInterpreterEntry(executablePath, arguments);
            var commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var batchArguments = new List<string>(arguments.Count + 3)
            {
                "/d",
                "/c",
                executablePath
            };

            batchArguments.AddRange(arguments);

            return (commandInterpreter, batchArguments);
        }

        return (executablePath, arguments.ToArray());
    }

    public static bool IsBatchFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);

        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }
}
