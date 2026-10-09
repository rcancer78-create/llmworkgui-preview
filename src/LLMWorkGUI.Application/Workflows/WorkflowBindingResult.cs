using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowBindingResult(
    WorkflowBinding Binding,
    WorkflowPackage Package,
    WorkflowVersion ActiveVersion,
    bool IsNewBinding);
