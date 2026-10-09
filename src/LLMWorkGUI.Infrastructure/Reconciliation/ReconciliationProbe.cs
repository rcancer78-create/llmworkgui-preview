using System.Diagnostics;
using System.Globalization;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Infrastructure.Cli;
using BackendType = LLMWorkGUI.Domain.Enums.BackendType;

namespace LLMWorkGUI.Infrastructure.Reconciliation;

public sealed class ReconciliationProbe : IReconciliationProbe
{
    private const string ProcessIdToken = "pid:";
    private const string ProcessNameToken = "name:";

    private readonly ICliExecutableLocator _executableLocator;

    public ReconciliationProbe(ICliExecutableLocator executableLocator)
    {
        ArgumentNullException.ThrowIfNull(executableLocator);

        _executableLocator = executableLocator;
    }

    public async Task<ReconciliationProbeResult> ProbeAsync(
        ReconciliationProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var executableName = ResolveExecutableName(request.Session.Binding.Backend);
        if (executableName is null)
        {
            // Remote and separately recovered backends have no generic CLI process proof.
            // Preserve uncertainty through the recovery matrix instead of crashing startup.
            return new ReconciliationProbeResult
            {
                BackendAvailable = false,
                ProcessAlive = false,
                ObservedNativeSessionId = null,
                ObservedBinding = null,
                Details = "This backend has no generic executable reconciliation probe."
            };
        }
        var executablePath = await _executableLocator
            .LocateAsync(executableName, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ReconciliationProbeResult
            {
                BackendAvailable = false,
                ProcessAlive = false,
                ObservedNativeSessionId = null,
                ObservedBinding = null,
                Details = $"Backend executable '{executableName}' is not available."
            };
        }

        var (processAlive, processDetails) = ResolveProcessLiveness(request.ActiveExecution?.ProcessState);

        if (!processAlive)
        {
            return new ReconciliationProbeResult
            {
                BackendAvailable = true,
                ProcessAlive = false,
                ObservedNativeSessionId = null,
                ObservedBinding = null,
                Details = processDetails
            };
        }

        return new ReconciliationProbeResult
        {
            BackendAvailable = true,
            ProcessAlive = true,
            ObservedNativeSessionId = null,
            ObservedBinding = null,
            Details = processDetails
        };
    }

    private static string? ResolveExecutableName(BackendType backend)
    {
        return backend switch
        {
            BackendType.OpenCode => CliDetectionService.OpenCodeExecutableName,
            BackendType.CursorAcp => CliDetectionService.CursorAgentExecutableName,
            _ => null
        };
    }

    private static (bool Alive, string Details) ResolveProcessLiveness(string? processState)
    {
        if (string.IsNullOrWhiteSpace(processState))
        {
            return (false, "No persisted process descriptor is available; process liveness cannot be proven.");
        }

        var processIdText = ExtractToken(processState, ProcessIdToken);

        if (!int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || processId <= 0)
        {
            return (false, "The persisted process state has no verifiable process identifier; process liveness cannot be proven.");
        }

        try
        {
            using var process = Process.GetProcessById(processId);

            var expectedName = ExtractToken(processState, ProcessNameToken);

            if (!string.IsNullOrWhiteSpace(expectedName))
            {
                var actualName = process.ProcessName;
                var normalizedName = Path.GetFileNameWithoutExtension(expectedName);

                if (!string.Equals(actualName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Process {processId} is running as '{actualName}' instead of '{normalizedName}'.");
                }
            }

            return (true, $"Process {processId} is alive.");
        }
        catch (ArgumentException)
        {
            return (false, $"Process {processId} is no longer running.");
        }
        catch (InvalidOperationException)
        {
            return (false, $"Process {processId} is no longer running.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, $"Process {processId} could not be inspected.");
        }
    }

    private static string? ExtractToken(string value, string token)
    {
        var index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return null;
        }

        var start = index + token.Length;
        var end = value.IndexOf(';', start);
        var raw = end < 0 ? value[start..] : value[start..end];
        var trimmed = raw.Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }
}
