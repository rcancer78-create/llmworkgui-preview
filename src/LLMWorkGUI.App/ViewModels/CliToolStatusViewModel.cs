using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class CliToolStatusViewModel
{
    public CliToolStatusViewModel(CliStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        DisplayName = status.DisplayName;
        ExecutableName = status.ExecutableName;
        IsDetected = status.IsDetected;
        StatusDisplay = status.StatusDisplay;
        PathDisplay = status.PathDisplay;
        DetectionError = status.DetectionError;
        Backend = status.Backend;
    }

    public BackendType Backend { get; }

    public string DisplayName { get; }

    public string ExecutableName { get; }

    public bool IsDetected { get; }

    public string StatusDisplay { get; }

    public string PathDisplay { get; }

    public string? DetectionError { get; }

    public bool HasDetectionError => DetectionError is not null;

    public string Tooltip => HasDetectionError
        ? $"{DisplayName} detection failed: {DetectionError}"
        : $"{DisplayName} ({ExecutableName}): {PathDisplay}";
}
