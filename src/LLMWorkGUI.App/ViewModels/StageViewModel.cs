using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class StageViewModel
{
    public StageViewModel(string label, WorkflowRole role, ExecutionState state, bool isCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Label = label;
        Role = role;
        State = state;
        IsCurrent = isCurrent;
    }

    public string Label { get; }

    public WorkflowRole Role { get; }

    public ExecutionState State { get; }

    public bool IsCurrent { get; }

    public string RoleDisplay => Role.ToString();

    public string StateDisplay => State.ToString();
}
