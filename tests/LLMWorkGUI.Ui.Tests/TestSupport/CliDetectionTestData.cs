using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal static class CliDetectionTestData
{
    public static CliDetectionSnapshot AllDetected(DateTimeOffset detectedAtUtc)
    {
        return new CliDetectionSnapshot(
            new[]
            {
                CliStatus.Detected(BackendType.OpenCode, "opencode", "OpenCode", @"C:\tools\opencode.exe"),
                CliStatus.Detected(BackendType.CursorAcp, "cursor-agent", "Cursor Agent", @"C:\tools\cursor-agent.cmd")
            },
            detectedAtUtc);
    }

    public static CliDetectionSnapshot OpenCodeOnly(DateTimeOffset detectedAtUtc)
    {
        return new CliDetectionSnapshot(
            new[]
            {
                CliStatus.Detected(BackendType.OpenCode, "opencode", "OpenCode", @"C:\tools\opencode.exe"),
                CliStatus.NotDetected(BackendType.CursorAcp, "cursor-agent", "Cursor Agent")
            },
            detectedAtUtc);
    }
}
