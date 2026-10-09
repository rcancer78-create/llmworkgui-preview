using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Cli;

public sealed class CliDetectionService : ICliDetectionService
{
    public const string OpenCodeExecutableName = "opencode";
    public const string CursorAgentExecutableName = "cursor-agent";

    private readonly ICliExecutableLocator _executableLocator;
    private readonly TimeProvider _timeProvider;

    public CliDetectionService(ICliExecutableLocator executableLocator, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executableLocator);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _executableLocator = executableLocator;
        _timeProvider = timeProvider;
    }

    public async Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        var openCode = await DetectToolAsync(
            BackendType.OpenCode,
            OpenCodeExecutableName,
            "OpenCode",
            cancellationToken).ConfigureAwait(false);

        var cursorAcp = await DetectToolAsync(
            BackendType.CursorAcp,
            CursorAgentExecutableName,
            "Cursor Agent",
            cancellationToken).ConfigureAwait(false);

        return new CliDetectionSnapshot(new[] { openCode, cursorAcp }, _timeProvider.GetUtcNow());
    }

    private async Task<CliStatus> DetectToolAsync(
        BackendType backend,
        string executableName,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedPath = await _executableLocator
                .LocateAsync(executableName, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(resolvedPath)
                ? CliStatus.NotDetected(backend, executableName, displayName)
                : CliStatus.Detected(backend, executableName, displayName, resolvedPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CliStatus.DetectionFailed(backend, executableName, displayName, exception.Message);
        }
    }
}
