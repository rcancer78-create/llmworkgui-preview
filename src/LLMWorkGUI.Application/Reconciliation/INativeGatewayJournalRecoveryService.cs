namespace LLMWorkGUI.Application.Reconciliation;

public interface INativeGatewayJournalRecoveryService
{
    Task QuarantineInterruptedAsync(CancellationToken cancellationToken = default);
    Task<int> ReplayActivityAsync(CancellationToken cancellationToken = default);
    Task<ReconciliationEvidence?> TryReconcileAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string sessionId, RecoveryAction action, CancellationToken cancellationToken = default);
}
