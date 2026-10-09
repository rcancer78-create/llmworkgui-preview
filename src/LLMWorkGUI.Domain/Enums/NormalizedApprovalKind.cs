namespace LLMWorkGUI.Domain.Enums;

public enum NormalizedApprovalKind
{
    ReadFile,
    WriteFile,
    ShellCommand,
    NetworkTool,
    WorkspaceExpansion,
    HighRiskDestructive,
    UnknownHighRisk
}
