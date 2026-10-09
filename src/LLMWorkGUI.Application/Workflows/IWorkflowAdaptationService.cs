using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Model-assisted workflow adaptation orchestration. Every method is executed only on an explicit
/// user command: no model call, candidate save, activation, or cleanup happens in the background.
/// </summary>
public interface IWorkflowAdaptationService
{
    /// <summary>
    /// Builds the read-only pre-send preview. <paramref name="allowExpandedSemanticScope"/> is the scope
    /// the user selected, and it is reflected verbatim in the previewed prompt.
    /// </summary>
    Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
        string workflowVersionId,
        string adapterRouteId,
        AdaptationGoal goal,
        IReadOnlyList<string>? userExcludedFiles = null,
        bool allowExpandedSemanticScope = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the first adaptation turn: builds the sanitized prompt, invokes the adapter model,
    /// applies the returned file modifications in scratch and validates the candidate. The source
    /// blob and active binding remain untouched.
    /// </summary>
    Task<AdaptationCandidateResult> StartAdaptationAsync(
        AdaptationExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Continues an existing in-memory adaptation session. The candidate workspace is recreated from
    /// the immutable source blob before the latest turn modifications are applied, so no stale file
    /// from a previous turn can leak into the new candidate.
    /// Every response supplies the complete desired modification map relative to that source;
    /// retaining a prior edit requires including the file again in the latest response.
    /// </summary>
    Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
        AdaptationFollowUpRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Packages the current candidate scratch files into a deterministic ZIP blob and registers a
    /// new <see cref="Domain.Enums.WorkflowSourceType.SyntheticDraft"/> version with an incremented
    /// version number that is never activated. Saving is allowed while blockers exist.
    /// A committed result with CleanupPending retains the session for cleanup; retrying save returns
    /// the same version identity without inserting another version.
    /// </summary>
    Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Cleans up scratch and releases the session. Idempotent when a prior close or expiry already removed it.</summary>
    Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    AdaptationSessionSnapshot GetSessionSnapshot(string sessionId);
}
