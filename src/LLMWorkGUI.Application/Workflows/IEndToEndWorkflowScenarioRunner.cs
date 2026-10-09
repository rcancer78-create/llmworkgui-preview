namespace LLMWorkGUI.Application.Workflows;

/// <summary>Stable identifiers of the end-to-end acceptance blocker proofs.</summary>
public static class EndToEndScenarioBlockerIds
{
    public const string MissingReviewer = "missing-reviewer";
    public const string ConflictingVerdicts = "conflicting-verdicts";
    public const string HashMismatch = "hash-mismatch";
    public const string MissingUiArtifact = "missing-ui-artifact";
}

/// <summary>
/// One separately proven blocker of the end-to-end acceptance scenario. A blocked transition is only
/// accepted as evidence when the specific check failed while the other checks did not, so the scenario
/// proves each fail-closed condition on its own.
/// </summary>
public sealed record EndToEndScenarioBlockerEvidence(
    string BlockerId,
    string DisplayName,
    string BlockedTransitionId,
    bool IsBlocked,
    string Detail);

/// <summary>
/// The outcome of the end-to-end hardening acceptance scenario: every fail-closed blocker is proven
/// separately, the reworked documents pass the gate, the AGY/Codex account-context switch is refused
/// for the named missing proof while the Mirasim host stays untouched, and a failed run recovers to
/// completion (ROADMAP Phase 12 exit criteria).
///
/// The scenario never claims a native switch. <see cref="AgyProfileSwitchRefused"/> and
/// <see cref="CodexHomeSwitchRefused"/> are the honest expectations on a host without a backend that
/// reports the account, the actual model, a unique route key and the native session; there is no field
/// that a caller could set to a "switch happened" claim.
/// </summary>
public sealed record EndToEndWorkflowScenarioReport
{
    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset CompletedAtUtc { get; init; }

    public required IReadOnlyList<EndToEndScenarioBlockerEvidence> BlockerEvidences { get; init; }

    public required bool ReworkApprovalAllowed { get; init; }

    /// <summary>
    /// The AGY profile switch was refused with the named missing proof and created no native session.
    /// A green result here means the refusal was proven, not that a switch happened.
    /// </summary>
    public required bool AgyProfileSwitchRefused { get; init; }

    /// <summary>
    /// The Codex <c>CODEX_HOME</c> switch was refused with the named missing proof and created no native
    /// session. A green result here means the refusal was proven, not that a switch happened.
    /// </summary>
    public required bool CodexHomeSwitchRefused { get; init; }

    /// <summary>
    /// The refusal the scenario observed named the missing executable and the missing independent
    /// identity/session wire contract, instead of quietly reporting a switch.
    /// </summary>
    public required bool NativeSwitchProofRefusalNamed { get; init; }

    public required bool MirasimHostUntouched { get; init; }

    public required bool RunFailureRecovered { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool ConflictingVerdictsBlocked =>
        IsBlocked(EndToEndScenarioBlockerIds.ConflictingVerdicts);

    public bool MissingReviewerBlocked =>
        IsBlocked(EndToEndScenarioBlockerIds.MissingReviewer);

    public bool HashMismatchBlocked =>
        IsBlocked(EndToEndScenarioBlockerIds.HashMismatch);

    public bool MissingUiArtifactBlocked =>
        IsBlocked(EndToEndScenarioBlockerIds.MissingUiArtifact);

    /// <summary>
    /// A native account-context switch is never proven by this scenario, so this is a constant: no
    /// shipped indicator may read it as a verified native switch.
    /// </summary>
    public bool NativeSwitchVerified => false;

    public bool IsSuccessful =>
        BlockerEvidences.Count == 4
        && MissingReviewerBlocked
        && ConflictingVerdictsBlocked
        && HashMismatchBlocked
        && MissingUiArtifactBlocked
        && ReworkApprovalAllowed
        && AgyProfileSwitchRefused
        && CodexHomeSwitchRefused
        && NativeSwitchProofRefusalNamed
        && MirasimHostUntouched
        && RunFailureRecovered
        && Warnings.Count == 0;

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public EndToEndScenarioBlockerEvidence? FindBlocker(string blockerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockerId);

        return BlockerEvidences.FirstOrDefault(
            evidence => string.Equals(evidence.BlockerId, blockerId, StringComparison.Ordinal));
    }

    public bool IsBlocked(string blockerId) => FindBlocker(blockerId)?.IsBlocked ?? false;

    public string Summary => IsSuccessful
        ? "End-to-end hardening scenario passed: all four fail-closed blockers, the rework approval, the "
            + "named refusal of the AGY/Codex account-context switch with no native session, the untouched "
            + "Mirasim host and the recovered run are proven. No native account-context switch was "
            + "verified: this host has no backend that reports the account, the actual model, a unique "
            + "route key and the native session."
        : "End-to-end hardening scenario failed: "
            + string.Join("; ", Warnings.Count > 0 ? Warnings : new[] { "one or more proofs are missing" });
}

/// <summary>
/// The deterministic end-to-end acceptance scenario runner of the hardening phase. It exercises the
/// pre-coder gate blockers, the refusal of the AGY/Codex account-context switch and run
/// failure/recovery on deterministic stubs only; no live model call is ever made and no native
/// account-context switch is ever claimed (ROADMAP Phase 12).
/// </summary>
public interface IEndToEndWorkflowScenarioRunner
{
    Task<EndToEndWorkflowScenarioReport> RunAsync(CancellationToken cancellationToken = default);
}
