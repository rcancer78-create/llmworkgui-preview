using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Cli;

public sealed class CliStatus
{
    public const string NotDetectedDisplay = "Не обнаружен";

    private CliStatus(
        BackendType backend,
        string executableName,
        string displayName,
        bool isDetected,
        string? resolvedPath,
        string? detectionError)
    {
        Backend = backend;
        ExecutableName = ApplicationGuard.NotBlank(executableName, nameof(executableName));
        DisplayName = ApplicationGuard.NotBlank(displayName, nameof(displayName));
        IsDetected = isDetected;
        ResolvedPath = ApplicationGuard.OptionalNotBlank(resolvedPath, nameof(resolvedPath));
        DetectionError = ApplicationGuard.OptionalNotBlank(detectionError, nameof(detectionError));

        if (isDetected && ResolvedPath is null)
        {
            throw new ArgumentException(
                "A detected executable must report the resolved path.",
                nameof(resolvedPath));
        }

        if (!isDetected && ResolvedPath is not null)
        {
            throw new ArgumentException(
                "An undetected executable must not report a resolved path.",
                nameof(resolvedPath));
        }
    }

    public BackendType Backend { get; }

    public string ExecutableName { get; }

    public string DisplayName { get; }

    public bool IsDetected { get; }

    public string? ResolvedPath { get; }

    public string? DetectionError { get; }

    public string StatusDisplay => IsDetected ? "Обнаружен" : NotDetectedDisplay;

    public string PathDisplay => ResolvedPath ?? "Не найден в PATH";

    public static CliStatus Detected(
        BackendType backend,
        string executableName,
        string displayName,
        string resolvedPath)
    {
        return new CliStatus(backend, executableName, displayName, isDetected: true, resolvedPath, null);
    }

    public static CliStatus NotDetected(BackendType backend, string executableName, string displayName)
    {
        return new CliStatus(backend, executableName, displayName, isDetected: false, null, null);
    }

    public static CliStatus DetectionFailed(
        BackendType backend,
        string executableName,
        string displayName,
        string detectionError)
    {
        return new CliStatus(backend, executableName, displayName, isDetected: false, null, detectionError);
    }
}
