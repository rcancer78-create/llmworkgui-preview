using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Snapshot of one adaptation turn: the parsed model response, every candidate validation result and
/// the package diff. The result is informational only; the source version and active binding are
/// never mutated by the engine.
/// </summary>
public sealed record AdaptationCandidateResult
{
    public AdaptationCandidateResult(
        string sessionId,
        string sourceVersionId,
        int sourceVersionNumber,
        string adapterRouteId,
        string adapterModelId,
        AdaptationGoal goal,
        bool allowExpandedSemanticScope,
        string candidateWorkspacePath,
        IReadOnlyList<SemanticRoleMapping> mappings,
        string rationale,
        IReadOnlyList<string> warnings,
        IReadOnlyList<AdaptationBlockerKind> blockers,
        IReadOnlyList<AdaptationValidationIssue> blockingIssues,
        WorkflowSecretScanReport secretScanReport,
        AdaptationReferenceValidationResult referenceValidation,
        SemanticDiffResult semanticDiff,
        WorkflowPackageDiff packageDiff,
        IReadOnlyDictionary<string, string> fileModifications,
        IReadOnlyList<string> candidateFiles,
        string? parseError)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(blockers);
        ArgumentNullException.ThrowIfNull(blockingIssues);
        ArgumentNullException.ThrowIfNull(fileModifications);
        ArgumentNullException.ThrowIfNull(candidateFiles);

        SessionId = ApplicationGuard.NotBlank(sessionId, nameof(sessionId));
        SourceVersionId = ApplicationGuard.NotBlank(sourceVersionId, nameof(sourceVersionId));
        SourceVersionNumber = sourceVersionNumber;
        AdapterRouteId = ApplicationGuard.NotBlank(adapterRouteId, nameof(adapterRouteId));
        AdapterModelId = ApplicationGuard.NotBlank(adapterModelId, nameof(adapterModelId));
        Goal = goal;
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
        CandidateWorkspacePath = ApplicationGuard.NotBlank(candidateWorkspacePath, nameof(candidateWorkspacePath));
        Mappings = mappings.ToArray();
        Rationale = rationale ?? throw new ArgumentNullException(nameof(rationale));
        Warnings = warnings.ToArray();
        Blockers = blockers.ToArray();
        BlockingIssues = blockingIssues.ToArray();
        SecretScanReport = secretScanReport ?? throw new ArgumentNullException(nameof(secretScanReport));
        ReferenceValidation = referenceValidation ?? throw new ArgumentNullException(nameof(referenceValidation));
        SemanticDiff = semanticDiff ?? throw new ArgumentNullException(nameof(semanticDiff));
        PackageDiff = packageDiff ?? throw new ArgumentNullException(nameof(packageDiff));
        FileModifications = new Dictionary<string, string>(fileModifications, StringComparer.Ordinal);
        CandidateFiles = candidateFiles.ToArray();
        ParseError = parseError;
    }

    public string SessionId { get; }

    public string SourceVersionId { get; }

    public int SourceVersionNumber { get; }

    public string AdapterRouteId { get; }

    public string AdapterModelId { get; }

    public AdaptationGoal Goal { get; }

    /// <summary>
    /// The pre-send scope flag this session ran with. It is reported for diagnostics and prompt context
    /// only: every semantic difference is still a blocker in <see cref="BlockingIssues"/>.
    /// </summary>
    public bool AllowExpandedSemanticScope { get; }

    public string CandidateWorkspacePath { get; }

    public IReadOnlyList<SemanticRoleMapping> Mappings { get; }

    public string Rationale { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<AdaptationBlockerKind> Blockers { get; }

    public IReadOnlyList<AdaptationValidationIssue> BlockingIssues { get; }

    public WorkflowSecretScanReport SecretScanReport { get; }

    public AdaptationReferenceValidationResult ReferenceValidation { get; }

    public SemanticDiffResult SemanticDiff { get; }

    public WorkflowPackageDiff PackageDiff { get; }

    public IReadOnlyDictionary<string, string> FileModifications { get; }

    public IReadOnlyList<string> CandidateFiles { get; }

    public string? ParseError { get; }

    public bool HasBlockers => Blockers.Count > 0
        || BlockingIssues.Count > 0
        || SecretScanReport.HasFindings
        || !ReferenceValidation.IsValid
        || SemanticDiff.HasBlockers
        || !string.IsNullOrWhiteSpace(ParseError);
}
