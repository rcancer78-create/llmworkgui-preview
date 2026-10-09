namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowSecretScanReport
{
    public static readonly WorkflowSecretScanReport Empty =
        new(false, Array.Empty<WorkflowSecretFinding>(), Array.Empty<string>());

    public WorkflowSecretScanReport(
        bool hasFindings,
        IReadOnlyList<WorkflowSecretFinding> findings,
        IReadOnlyList<string> recommendedExcludedFiles)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(recommendedExcludedFiles);

        HasFindings = hasFindings || findings.Count > 0;
        Findings = findings.ToArray();
        RecommendedExcludedFiles = recommendedExcludedFiles.ToArray();
    }

    public bool HasFindings { get; }

    public IReadOnlyList<WorkflowSecretFinding> Findings { get; }

    public IReadOnlyList<string> RecommendedExcludedFiles { get; }
}
