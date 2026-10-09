using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// Fail-closed pre-coder gate. Every required document must carry a current-hash unanimous
/// <see cref="WorkflowReviewVerdict.Approve"/> from every required reviewer role, an explicit user
/// approval on the same hash and the UI acceptance evidence when the template requires it. A route that
/// is missing or mismatching the request blocks the transition as well.
/// </summary>
public sealed class PreCoderGateValidator : IPreCoderGateValidator
{
    public PreCoderGateValidationResult Evaluate(PreCoderGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var documentsByKind = new Dictionary<DocumentTemplateKind, PreCoderGateDocument>();
        var duplicateKinds = new HashSet<DocumentTemplateKind>();

        foreach (var document in request.Documents)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (!documentsByKind.TryAdd(document.Kind, document)) duplicateKinds.Add(document.Kind);
        }

        var requiredKinds = request.RequiredDocumentKinds.Distinct().ToArray();
        var requiredRoles = request.RequiredReviewerRoles.Distinct(StringComparer.Ordinal).ToArray();

        var missingDocuments = requiredKinds
            .Where(kind => !documentsByKind.ContainsKey(kind))
            .ToArray();

        var missingReviewers = new List<string>();
        var conflictingVerdicts = new List<string>();
        var nonApprovedVerdicts = new List<string>();
        var hashMismatches = new List<string>();
        var missingApprovals = new List<string>();

        foreach (var kind in requiredKinds)
        {
            if (!documentsByKind.TryGetValue(kind, out var document))
            {
                continue;
            }

            foreach (var verdict in document.ReviewerVerdicts)
            {
                if (verdict is not null
                    && requiredRoles.Contains(verdict.ReviewerRole, StringComparer.Ordinal)
                    && !string.Equals(verdict.DocumentHash, document.ContentHash, StringComparison.Ordinal))
                {
                    hashMismatches.Add($"{kind}:{verdict?.ReviewerRole ?? "unknown"}");
                }
            }

            foreach (var role in requiredRoles)
            {
                var roleVerdicts = document.ReviewerVerdicts
                    .Where(verdict => verdict is not null
                        && string.Equals(verdict.ReviewerRole, role, StringComparison.Ordinal))
                    .ToArray();

                if (roleVerdicts.Length == 0)
                {
                    missingReviewers.Add($"{kind}:{role}");
                    continue;
                }

                var onCurrentHash = roleVerdicts
                    .Where(verdict => string.Equals(
                        verdict.DocumentHash,
                        document.ContentHash,
                        StringComparison.Ordinal))
                    .ToArray();

                if (onCurrentHash.Length == 0)
                {
                    missingReviewers.Add($"{kind}:{role}");
                    continue;
                }

                var values = onCurrentHash.Select(verdict => verdict.Verdict).Distinct().ToArray();
                var hasApprove = values.Contains(WorkflowReviewVerdict.Approve);
                var hasBlocking = values.Any(verdict =>
                    verdict is WorkflowReviewVerdict.Reject or WorkflowReviewVerdict.RequestChanges);

                if (hasApprove && hasBlocking)
                {
                    conflictingVerdicts.Add($"{kind}:{role}");
                }

                var latest = onCurrentHash
                    .OrderByDescending(verdict => verdict.RecordedAtUtc)
                    .First();

                if (latest.Verdict != WorkflowReviewVerdict.Approve)
                {
                    nonApprovedVerdicts.Add($"{kind}:{role}={latest.Verdict}");
                }
            }

            var approval = document.UserApproval;

            if (approval is null
                || approval.Decision != UserApprovalDecision.Approved
                || !string.Equals(approval.ArtifactHash, document.ContentHash, StringComparison.Ordinal))
            {
                missingApprovals.Add(kind.ToString());
            }
        }

        var hasMissingRequiredDocument = missingDocuments.Length > 0;
        var hasMissingReviewer = missingReviewers.Count > 0;
        var hasConflictingVerdicts = conflictingVerdicts.Count > 0;
        var isHashMismatch = hashMismatches.Count > 0;
        var hasMissingUserApproval = missingApprovals.Count > 0;
        var isRouteMismatch = EvaluateRouteMismatch(request);
        var requiredDocuments = requiredKinds
            .Where(documentsByKind.ContainsKey)
            .Select(kind => documentsByKind[kind])
            .ToArray();
        var hasMissingUiArtifact = request.RequiresUiArtifact
            && !requiredDocuments.Any(document => document.HasUiArtifact);
        var hasMissingVisualAcceptance = request.RequiresUserVisualAcceptance
            && !requiredDocuments.Any(document => document.HasVisualAcceptance);

        var reasons = new List<string>();

        var ambiguousRequiredKinds = requiredKinds.Where(duplicateKinds.Contains).ToArray();
        if (ambiguousRequiredKinds.Length > 0)
        {
            // This request contains no policy for selecting a revision. Picking the first would hide
            // a competing required document and make the planning diagnosis depend on list order.
            reasons.Add($"duplicate required document kind(s): {string.Join(", ", ambiguousRequiredKinds)}");
        }

        if (hasMissingRequiredDocument)
        {
            reasons.Add($"missing required document(s): {string.Join(", ", missingDocuments)}");
        }

        if (hasMissingReviewer)
        {
            reasons.Add(
                $"missing reviewer verdict(s) on the current hash: {string.Join(", ", missingReviewers)}");
        }

        if (hasConflictingVerdicts)
        {
            reasons.Add(
                "conflicting reviewer verdicts (approve paired with reject/request-changes): "
                + string.Join(", ", conflictingVerdicts));
        }

        if (nonApprovedVerdicts.Count > 0)
        {
            reasons.Add(
                $"reviewer verdict(s) are not Approve on the current hash: {string.Join(", ", nonApprovedVerdicts)}");
        }

        if (isHashMismatch)
        {
            reasons.Add(
                "document hash changed after review for: "
                + string.Join(", ", hashMismatches.Distinct(StringComparer.Ordinal)));
        }

        if (hasMissingUserApproval)
        {
            reasons.Add(
                $"missing user approval on the current hash for: {string.Join(", ", missingApprovals)}");
        }

        if (isRouteMismatch)
        {
            reasons.Add(
                $"route evidence is missing or mismatched (requested '{request.RequestedRouteId ?? "none"}', "
                + $"observed '{request.ObservedRouteId ?? "not reported"}')");
        }

        if (hasMissingUiArtifact)
        {
            reasons.Add("the required UI artifact is missing");
        }

        if (hasMissingVisualAcceptance)
        {
            reasons.Add("the required user visual acceptance is missing");
        }

        var checks = BuildChecks(
            request,
            missingDocuments,
            missingReviewers,
            conflictingVerdicts,
            nonApprovedVerdicts,
            hashMismatches,
            missingApprovals,
            isRouteMismatch,
            hasMissingUiArtifact,
            hasMissingVisualAcceptance);

        var requiresEscalation = hasConflictingVerdicts
            || (nonApprovedVerdicts.Count > 0 && hasMissingUserApproval);

        return PreCoderGateValidationResult.Create(
            hasMissingRequiredDocument,
            hasMissingReviewer,
            hasConflictingVerdicts,
            isHashMismatch,
            isRouteMismatch,
            hasMissingUserApproval,
            hasMissingUiArtifact,
            hasMissingVisualAcceptance,
            request.BlockedTransitionId,
            requiresEscalation,
            request.EscalationTargetNodeId,
            checks,
            reasons);
    }

    private static bool EvaluateRouteMismatch(PreCoderGateRequest request)
    {
        if (request.RequestedRouteId is null && request.ObservedRouteId is null)
        {
            return false;
        }

        if (request.RequestedRouteId is null || request.ObservedRouteId is null)
        {
            return true;
        }

        return !string.Equals(
            request.RequestedRouteId,
            request.ObservedRouteId,
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<PreCoderGateCheck> BuildChecks(
        PreCoderGateRequest request,
        IReadOnlyList<DocumentTemplateKind> missingDocuments,
        IReadOnlyList<string> missingReviewers,
        IReadOnlyList<string> conflictingVerdicts,
        IReadOnlyList<string> nonApprovedVerdicts,
        IReadOnlyList<string> hashMismatches,
        IReadOnlyList<string> missingApprovals,
        bool isRouteMismatch,
        bool hasMissingUiArtifact,
        bool hasMissingVisualAcceptance)
    {
        var requiredCount = request.RequiredDocumentKinds.Distinct().Count();

        var requiredDocumentsCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.RequiredDocuments,
            "Обязательные документы",
            missingDocuments.Count == 0,
            missingDocuments.Count == 0
                ? $"All {requiredCount} required document(s) are present."
                : $"Missing: {string.Join(", ", missingDocuments)}");

        var reviewerUnanimitySatisfied = missingReviewers.Count == 0
            && conflictingVerdicts.Count == 0
            && nonApprovedVerdicts.Count == 0;

        var reviewerProblems = new List<string>();

        if (missingReviewers.Count > 0)
        {
            reviewerProblems.Add($"missing: {string.Join(", ", missingReviewers)}");
        }

        if (conflictingVerdicts.Count > 0)
        {
            reviewerProblems.Add($"conflicting: {string.Join(", ", conflictingVerdicts)}");
        }

        if (nonApprovedVerdicts.Count > 0)
        {
            reviewerProblems.Add($"not approved: {string.Join(", ", nonApprovedVerdicts)}");
        }

        var reviewerDetail = reviewerUnanimitySatisfied
            ? "Every required reviewer approved the current hash of every required document."
            : string.Join("; ", reviewerProblems);

        var reviewerCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.ReviewerUnanimity,
            "Единогласный approve ревьюеров",
            reviewerUnanimitySatisfied,
            reviewerDetail);

        var userApprovalCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.UserApproval,
            "Утверждение пользователя",
            missingApprovals.Count == 0,
            missingApprovals.Count == 0
                ? "The user approved the current hash of every required document."
                : $"Missing on: {string.Join(", ", missingApprovals)}");

        var hashCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.HashIntegrity,
            "Целостность хэшей",
            hashMismatches.Count == 0,
            hashMismatches.Count == 0
                ? "Every required reviewer verdict references the current document hash."
                : $"Superseded hash after review: {string.Join(", ", hashMismatches)}");

        var routeCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.RouteEvidence,
            "Подтверждённый маршрут",
            !isRouteMismatch,
            request.RequestedRouteId is null && request.ObservedRouteId is null
                ? "No route confirmation was requested for this transition."
                : isRouteMismatch
                    ? $"Requested '{request.RequestedRouteId ?? "none"}', observed "
                        + $"'{request.ObservedRouteId ?? "not reported"}'."
                    : $"Observed route '{request.ObservedRouteId}' matches the requested route.");

        var uiArtifactCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.UiArtifact,
            "UI-артефакт",
            !hasMissingUiArtifact,
            request.RequiresUiArtifact
                ? hasMissingUiArtifact
                    ? "The template requires a UI artifact, but none is present."
                    : "A UI artifact is present."
                : "The template does not require a UI artifact.");

        var visualAcceptanceCheck = new PreCoderGateCheck(
            PreCoderGateCheckIds.VisualAcceptance,
            "Визуальная приёмка",
            !hasMissingVisualAcceptance,
            request.RequiresUserVisualAcceptance
                ? hasMissingVisualAcceptance
                    ? "The template requires user visual acceptance, but none is recorded."
                    : "User visual acceptance is recorded."
                : "The template does not require user visual acceptance.");

        return new[]
        {
            requiredDocumentsCheck,
            reviewerCheck,
            userApprovalCheck,
            hashCheck,
            routeCheck,
            uiArtifactCheck,
            visualAcceptanceCheck
        };
    }
}
