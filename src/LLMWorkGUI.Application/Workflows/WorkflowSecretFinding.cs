namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowSecretFinding(
    string RelativePath,
    int LineNumber,
    string RuleName,
    string RedactedSnippet);
