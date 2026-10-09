using System.Diagnostics;
using LLMWorkGUI.Application.Agy;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Default <see cref="IAgyProcessInspector"/> based on the live process list.
/// A running <c>agy</c> process blocks any profile switch (ТЗ §6.4, §6.11a).
/// </summary>
public sealed class DefaultAgyProcessInspector : IAgyProcessInspector
{
    public const string AgyProcessName = "agy";

    public Task<bool> IsAgyRunningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var processes = Process.GetProcessesByName(AgyProcessName);

        try
        {
            return Task.FromResult(processes.Length > 0);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
