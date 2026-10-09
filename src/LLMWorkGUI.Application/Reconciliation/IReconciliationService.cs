namespace LLMWorkGUI.Application.Reconciliation;

public interface IReconciliationService
{
    Task<ReconciliationEvidence> ReconcileSessionAsync(
        string localSessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReconciliationEvidence>> ReconcileAllActiveAsync(
        CancellationToken cancellationToken = default);

    Task<ReconciliationRecoveryResult> ApplyRecoveryActionAsync(
        string localSessionId,
        RecoveryAction action,
        CancellationToken cancellationToken = default);
}
