namespace LLMWorkGUI.Application.Reconciliation;

/// <summary>Reconciles local journal ownership; never resends prompts or infers native identity from a PID.</summary>
public interface IOpenCodeJournalRecoveryService
{
    Task<IReadOnlyList<ReconciliationEvidence>> QuarantineInterruptedAsync(CancellationToken cancellationToken = default);
    Task<ReconciliationEvidence?> TryReconcileAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string sessionId, RecoveryAction action, CancellationToken cancellationToken = default);
    Task<int> ReplayActivityAsync(CancellationToken cancellationToken = default);
}
