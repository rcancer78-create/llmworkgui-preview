using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Application.Watchdogs;

/// <summary>
/// Deterministic evidence interpretation for non-zero process exits. Phase 2 has no
/// normalized protocol terminal events yet, so a failed process is classified from bounded
/// stderr/stdout evidence captured by the Process Supervisor. Adapters replace this evidence
/// with normalized terminal events in later phases.
/// </summary>
public static class ProcessFailureEvidence
{
    private static readonly string[] OrchestrationMarkers =
    {
        "missing required argument",
        "required argument is missing",
        "missing argument",
        "required argument",
        "argument is required",
        "отсутствует обязательный аргумент",
        "не указан обязательный аргумент"
    };

    private static readonly string[] InteractiveInputMarkers =
    {
        "stdin",
        "standard input",
        "interactive input",
        "console.in",
        "readline",
        "no input",
        "input is required",
        "input required",
        "press any key",
        "ожидание ввода",
        "ввод не получен"
    };

    public static bool IndicatesOrchestrationFailure(ProcessExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return ContainsMarker(result, OrchestrationMarkers);
    }

    public static bool IndicatesInteractiveInputWait(ProcessExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return ContainsMarker(result, InteractiveInputMarkers);
    }

    private static bool ContainsMarker(ProcessExecutionResult result, string[] markers)
    {
        var output = string.Concat(
            result.StandardErrorHead,
            result.StandardErrorTail,
            result.StandardOutputHead,
            result.StandardOutputTail);

        foreach (var marker in markers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
