using LLMWorkGUI.Application.Reconciliation;

namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>
/// Startup recovery for interrupted work: non-terminal executions and active workflow runs are moved
/// to an explicit failed/ambiguous state, checkout locks are released only when the linked work is no
/// longer ambiguous, and the health state machines are rehydrated from the append-only audit
/// (ROADMAP Phase 12, ТЗ §9.4).
/// </summary>
public interface IAppCrashRecoveryService
{
    Task<AppCrashRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovers workflow orchestration after startup reconciliation, without overwriting the native
    /// session/execution outcomes or releasing their locks. Only freshly reattached sessions from this
    /// startup may preserve a linked workflow; persisted reconciliation flags alone are insufficient.
    /// </summary>
    Task<AppCrashRecoveryReport> RecoverWorkflowRunsAfterRestartAsync(
        IReadOnlyList<ReconciliationEvidence> reconciliationEvidence,
        CancellationToken cancellationToken = default);
}
