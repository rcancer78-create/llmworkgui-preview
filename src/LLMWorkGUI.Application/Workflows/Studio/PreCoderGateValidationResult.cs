namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// The pre-coder gate verdict. Each blocking condition is reported separately so the UI can show which
/// check failed: a missing document, a missing reviewer, a conflicting approve/reject pair, a hash that
/// changed after review, an unproven route, or missing UI acceptance evidence.
/// </summary>
public sealed class PreCoderGateValidationResult
{
    private PreCoderGateValidationResult(
        bool isAllowed,
        bool hasMissingRequiredDocument,
        bool hasMissingReviewer,
        bool hasConflictingVerdicts,
        bool isHashMismatch,
        bool isRouteMismatch,
        bool hasMissingUserApproval,
        bool hasMissingUiArtifact,
        bool hasMissingVisualAcceptance,
        string blockedTransitionId,
        bool requiresEscalation,
        string? escalationTargetNodeId,
        IReadOnlyList<PreCoderGateCheck> checks,
        IReadOnlyList<string> blockingReasons,
        string summary)
    {
        IsAllowed = isAllowed;
        HasMissingRequiredDocument = hasMissingRequiredDocument;
        HasMissingReviewer = hasMissingReviewer;
        HasConflictingVerdicts = hasConflictingVerdicts;
        IsHashMismatch = isHashMismatch;
        IsRouteMismatch = isRouteMismatch;
        HasMissingUserApproval = hasMissingUserApproval;
        HasMissingUiArtifact = hasMissingUiArtifact;
        HasMissingVisualAcceptance = hasMissingVisualAcceptance;
        BlockedTransitionId = blockedTransitionId;
        RequiresEscalation = requiresEscalation;
        EscalationTargetNodeId = escalationTargetNodeId;
        Checks = checks;
        BlockingReasons = blockingReasons;
        Summary = summary;
    }

    public bool IsAllowed { get; }

    /// <summary>An input refusal in the planning UI; it makes no claim of missing native or persisted evidence.</summary>
    public static PreCoderGateValidationResult RefusedInput(string transitionId, string reason) => Create(
        false, false, false, false, false, false, false, false,
        ApplicationGuard.NotBlank(transitionId, nameof(transitionId)), false, null,
        Array.Empty<PreCoderGateCheck>(), new[] { ApplicationGuard.NotBlank(reason, nameof(reason)) });

    public bool HasMissingRequiredDocument { get; }

    public bool HasMissingReviewer { get; }

    public bool HasConflictingVerdicts { get; }

    public bool IsHashMismatch { get; }

    public bool IsRouteMismatch { get; }

    public bool HasMissingUserApproval { get; }

    public bool HasMissingUiArtifact { get; }

    public bool HasMissingVisualAcceptance { get; }

    /// <summary>The scheme transition this verdict guards.</summary>
    public string BlockedTransitionId { get; }

    /// <summary>True when conflicting verdicts must be routed to the named resolution node or the user.</summary>
    public bool RequiresEscalation { get; }

    /// <summary>The resolution node named by the scheme; null routes the conflict to the user.</summary>
    public string? EscalationTargetNodeId { get; }

    public string EscalationTargetDisplay => EscalationTargetNodeId ?? "user";

    public IReadOnlyList<PreCoderGateCheck> Checks { get; }

    public IReadOnlyList<string> BlockingReasons { get; }

    public string Summary { get; }

    public bool IsCheckSatisfied(string checkId) =>
        Checks.FirstOrDefault(check => string.Equals(check.CheckId, checkId, StringComparison.Ordinal))
            ?.IsSatisfied ?? false;

    internal static PreCoderGateValidationResult Create(
        bool hasMissingRequiredDocument,
        bool hasMissingReviewer,
        bool hasConflictingVerdicts,
        bool isHashMismatch,
        bool isRouteMismatch,
        bool hasMissingUserApproval,
        bool hasMissingUiArtifact,
        bool hasMissingVisualAcceptance,
        string blockedTransitionId,
        bool requiresEscalation,
        string? escalationTargetNodeId,
        IReadOnlyList<PreCoderGateCheck> checks,
        IReadOnlyList<string> blockingReasons)
    {
        var isAllowed = !hasMissingRequiredDocument
            && !hasMissingReviewer
            && !hasConflictingVerdicts
            && !isHashMismatch
            && !isRouteMismatch
            && !hasMissingUserApproval
            && !hasMissingUiArtifact
            && !hasMissingVisualAcceptance
            && checks.All(check => check.IsSatisfied)
            && blockingReasons.Count == 0;

        var summary = isAllowed
            ? $"The pre-coder gate allows transition '{blockedTransitionId}': every required document is "
                + "approved on the current hash and the route is confirmed."
            : $"The pre-coder gate blocks transition '{blockedTransitionId}': "
                + string.Join("; ", blockingReasons);

        if (requiresEscalation)
        {
            summary += " Conflicting verdicts are routed to '"
                + (escalationTargetNodeId ?? "user")
                + "'.";
        }

        return new PreCoderGateValidationResult(
            isAllowed,
            hasMissingRequiredDocument,
            hasMissingReviewer,
            hasConflictingVerdicts,
            isHashMismatch,
            isRouteMismatch,
            hasMissingUserApproval,
            hasMissingUiArtifact,
            hasMissingVisualAcceptance,
            blockedTransitionId,
            requiresEscalation,
            escalationTargetNodeId,
            checks,
            blockingReasons,
            summary);
    }
}
