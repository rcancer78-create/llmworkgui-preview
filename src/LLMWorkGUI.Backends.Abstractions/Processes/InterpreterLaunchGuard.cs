namespace LLMWorkGUI.Backends.Abstractions.Processes;

/// <summary>
/// Rejects batch and PowerShell entry points that the Windows command interpreter would
/// reparse. Direct executables stay on <c>ArgumentList</c> and are not passed through this guard.
/// </summary>
public static class InterpreterLaunchGuard
{
    private static readonly char[] Metacharacters = ['&', '|', '<', '>', '^', '%', '!', '\r', '\n', '"'];

    public static void RejectInterpreterEntry(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var extension = Path.GetExtension(executablePath);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A PowerShell script is not an allowed process entry point.",
                nameof(executablePath));
        }

        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Reject(executablePath, nameof(executablePath));
        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("A command argument is null.", nameof(arguments));
            }

            Reject(argument, nameof(arguments));
        }
    }

    private static void Reject(string value, string name)
    {
        if (value.IndexOfAny(Metacharacters) >= 0)
        {
            throw new ArgumentException(
                "The command contains a character that the Windows command interpreter would interpret.",
                name);
        }
    }
}
