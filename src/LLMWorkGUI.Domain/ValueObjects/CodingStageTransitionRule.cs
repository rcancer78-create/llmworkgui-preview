using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

public sealed class CodingStageTransitionRule
{
    public CodingStageTransitionRule(
        string stageId,
        IReadOnlyList<string>? requiredDocuments = null,
        IReadOnlyList<string>? requiredReviewerRoles = null,
        bool requiresDiffScopeCheck = true,
        bool requiresTestEvidence = false,
        bool requiresUiEvidence = false,
        int fixLimit = 0,
        string? escalationTargetNodeId = null)
    {
        StageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        RequiredDocuments = CopyValues(requiredDocuments, nameof(requiredDocuments));
        RequiredReviewerRoles = CopyValues(requiredReviewerRoles, nameof(requiredReviewerRoles));
        RequiresDiffScopeCheck = requiresDiffScopeCheck;
        RequiresTestEvidence = requiresTestEvidence;
        RequiresUiEvidence = requiresUiEvidence;
        FixLimit = GuardFixLimit(fixLimit, nameof(fixLimit));
        EscalationTargetNodeId = DomainGuard.OptionalNotBlank(
            escalationTargetNodeId,
            nameof(escalationTargetNodeId));
    }

    public string StageId { get; }

    public IReadOnlyList<string> RequiredDocuments { get; }

    public IReadOnlyList<string> RequiredReviewerRoles { get; }

    public bool RequiresDiffScopeCheck { get; }

    public bool RequiresTestEvidence { get; }

    public bool RequiresUiEvidence { get; }

    public int FixLimit { get; }

    public string? EscalationTargetNodeId { get; }

    public CodingStageTransitionDecision Evaluate(CodingStageTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var blockingReasons = new List<string>();

        if (evidence.FixIterations > FixLimit)
        {
            blockingReasons.Add(
                $"The fix limit of {FixLimit} iteration(s) is exceeded, so a forced escalation is required.");

            return CodingStageTransitionDecision.Escalate(blockingReasons, EscalationTargetNodeId);
        }

        foreach (var document in RequiredDocuments)
        {
            if (!evidence.PresentDocuments.Contains(document, StringComparer.Ordinal))
            {
                blockingReasons.Add($"Required document '{document}' is missing.");
            }
        }

        if (RequiresDiffScopeCheck && !evidence.DiffWithinScope)
        {
            blockingReasons.Add(
                "The diff/scope check did not confirm that the change stays within the declared scope.");
        }

        EvaluateReviewerVerdicts(evidence, blockingReasons);

        if (RequiresTestEvidence && !evidence.TestEvidencePresent)
        {
            blockingReasons.Add("The required test evidence is missing.");
        }

        if (RequiresUiEvidence)
        {
            if (!evidence.UiEvidencePresent)
            {
                blockingReasons.Add("The required visual UI evidence is missing.");
            }
            else if (string.IsNullOrWhiteSpace(evidence.UiEvidenceArtifactHash))
            {
                blockingReasons.Add("The visual UI evidence must pin the hash of the captured artifact.");
            }
        }

        return blockingReasons.Count == 0
            ? CodingStageTransitionDecision.Allow()
            : CodingStageTransitionDecision.Block(blockingReasons);
    }

    private void EvaluateReviewerVerdicts(
        CodingStageTransitionEvidence evidence,
        List<string> blockingReasons)
    {
        if (RequiredReviewerRoles.Count == 0)
        {
            return;
        }

        var documentHash = evidence.DocumentHash;

        if (documentHash is null)
        {
            blockingReasons.Add(
                "No reviewed document hash is reported, so the review check is incomplete.");
            return;
        }

        foreach (var reviewerRole in RequiredReviewerRoles)
        {
            var verdicts = evidence.ReviewerVerdicts
                .Where(verdict =>
                    string.Equals(verdict.ReviewerRole, reviewerRole, StringComparison.Ordinal)
                    && string.Equals(verdict.DocumentHash, documentHash, StringComparison.Ordinal))
                .ToArray();

            if (verdicts.Length == 0)
            {
                blockingReasons.Add(
                    $"The required reviewer '{reviewerRole}' has no verdict on document hash "
                    + $"'{documentHash}'; the review is incomplete.");
                continue;
            }

            if (verdicts.Any(verdict => verdict.Verdict != WorkflowReviewVerdict.Approve))
            {
                blockingReasons.Add(
                    $"The reviewer '{reviewerRole}' returned conflicting verdicts on document hash "
                    + $"'{documentHash}'; only a unanimous 'Approve' unlocks the transition.");
            }
        }
    }

    private static IReadOnlyList<string> CopyValues(
        IReadOnlyList<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var copy = new string[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = DomainGuard.NotBlank(values[index], parameterName);
        }

        return copy;
    }

    private static int GuardFixLimit(int fixLimit, string parameterName)
    {
        if (fixLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                fixLimit,
                "A fix iteration limit cannot be negative.");
        }

        return fixLimit;
    }
}

public sealed class CodingStageTransitionEvidence
{
    public CodingStageTransitionEvidence(
        IReadOnlyList<string>? presentDocuments = null,
        string? documentHash = null,
        IReadOnlyList<ReviewerVerdictRecord>? reviewerVerdicts = null,
        bool diffWithinScope = false,
        bool testEvidencePresent = false,
        bool uiEvidencePresent = false,
        string? uiEvidenceArtifactHash = null,
        int fixIterations = 0)
    {
        PresentDocuments = CopyDocuments(presentDocuments);
        DocumentHash = DomainGuard.OptionalNotBlank(documentHash, nameof(documentHash));
        ReviewerVerdicts = reviewerVerdicts?.ToArray() ?? Array.Empty<ReviewerVerdictRecord>();
        DiffWithinScope = diffWithinScope;
        TestEvidencePresent = testEvidencePresent;
        UiEvidencePresent = uiEvidencePresent;
        UiEvidenceArtifactHash = DomainGuard.OptionalNotBlank(
            uiEvidenceArtifactHash,
            nameof(uiEvidenceArtifactHash));
        FixIterations = GuardFixIterations(fixIterations, nameof(fixIterations));
    }

    public IReadOnlyList<string> PresentDocuments { get; }

    public string? DocumentHash { get; }

    public IReadOnlyList<ReviewerVerdictRecord> ReviewerVerdicts { get; }

    public bool DiffWithinScope { get; }

    public bool TestEvidencePresent { get; }

    public bool UiEvidencePresent { get; }

    public string? UiEvidenceArtifactHash { get; }

    public int FixIterations { get; }

    private static IReadOnlyList<string> CopyDocuments(IReadOnlyList<string>? documents)
    {
        if (documents is null)
        {
            return Array.Empty<string>();
        }

        return documents.ToArray();
    }

    private static int GuardFixIterations(int fixIterations, string parameterName)
    {
        if (fixIterations < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                fixIterations,
                "The fix iteration counter cannot be negative.");
        }

        return fixIterations;
    }
}

public sealed class CodingStageTransitionDecision
{
    private CodingStageTransitionDecision(
        bool isAllowed,
        bool requiresEscalation,
        IReadOnlyList<string> blockingReasons,
        string? escalationTargetNodeId)
    {
        IsAllowed = isAllowed;
        RequiresEscalation = requiresEscalation;
        BlockingReasons = blockingReasons;
        EscalationTargetNodeId = escalationTargetNodeId;
    }

    public bool IsAllowed { get; }

    public bool RequiresEscalation { get; }

    public IReadOnlyList<string> BlockingReasons { get; }

    public string? EscalationTargetNodeId { get; }

    public static CodingStageTransitionDecision Allow() =>
        new(
            isAllowed: true,
            requiresEscalation: false,
            Array.Empty<string>(),
            escalationTargetNodeId: null);

    public static CodingStageTransitionDecision Block(IReadOnlyList<string> blockingReasons)
    {
        ArgumentNullException.ThrowIfNull(blockingReasons);

        return new(
            isAllowed: false,
            requiresEscalation: false,
            blockingReasons.ToArray(),
            escalationTargetNodeId: null);
    }

    public static CodingStageTransitionDecision Escalate(
        IReadOnlyList<string> blockingReasons,
        string? escalationTargetNodeId)
    {
        ArgumentNullException.ThrowIfNull(blockingReasons);

        return new(
            isAllowed: false,
            requiresEscalation: true,
            blockingReasons.ToArray(),
            escalationTargetNodeId);
    }
}
