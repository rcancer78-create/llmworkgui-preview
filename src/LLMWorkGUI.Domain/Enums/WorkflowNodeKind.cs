namespace LLMWorkGUI.Domain.Enums;

public enum WorkflowNodeKind
{
    Prompt,
    Review,
    Writer,
    ApprovalGate,
    Condition,
    Retry,
    Escalation,
    UserDecision,
    ValidationCommand,
    ArtifactCollection,
    TerminalOutcome
}
