namespace LLMWorkGUI.Application.Workflows;

public enum WorkflowEntrypointKind
{
    PowerShell,
    Batch,
    Shell,
    Python,
    Other
}

public sealed record WorkflowEntrypointDescriptor(
    string Path,
    WorkflowEntrypointKind Kind,
    bool IsDeclared);
