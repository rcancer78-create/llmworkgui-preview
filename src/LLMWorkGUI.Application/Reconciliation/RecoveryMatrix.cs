using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.StateMachines;
using ExecutionState = LLMWorkGUI.Domain.Enums.ExecutionState;

namespace LLMWorkGUI.Application.Reconciliation;

public sealed class RecoveryMatrix
{
    public ReconciliationOutcome Classify(
        Session session,
        Execution? activeExecution,
        ReconciliationProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(probe);

        return ClassifyCore(session, activeExecution, probe);
    }

    public static bool IsPromptDeliveryExcluded(Session session, Execution? activeExecution)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (activeExecution is null)
        {
            return string.IsNullOrWhiteSpace(session.ActiveExecutionId);
        }

        return ReconciliationExecutionEvidence.IsPreDispatch(activeExecution);
    }

    private static ReconciliationOutcome ClassifyCore(
        Session session,
        Execution? activeExecution,
        ReconciliationProbeResult probe)
    {
        if (!probe.BackendAvailable)
        {
            return ReconciliationOutcome.BackendMissing;
        }

        var hasNativeSession = !string.IsNullOrWhiteSpace(session.NativeSessionId);
        var nativeSessionMatched = hasNativeSession
            && string.Equals(session.NativeSessionId, probe.ObservedNativeSessionId, StringComparison.Ordinal);
        var bindingMatched = probe.ObservedBinding is not null
            && session.Binding.Equals(probe.ObservedBinding);

        if (activeExecution?.State == ExecutionState.Ambiguous)
        {
            return ReconciliationOutcome.Ambiguous;
        }

        if (probe.ProcessAlive && nativeSessionMatched && bindingMatched)
        {
            return ReconciliationOutcome.Reattached;
        }

        if (activeExecution?.State == ExecutionState.RouteMismatch)
        {
            return ReconciliationOutcome.Orphaned;
        }

        if (IsPromptDeliveryExcluded(session, activeExecution))
        {
            return ReconciliationOutcome.Orphaned;
        }

        if (activeExecution is not null
            && ReconciliationExecutionEvidence.IsConfirmedTerminal(activeExecution)
            && !probe.ProcessAlive)
        {
            return ReconciliationOutcome.Orphaned;
        }

        return ReconciliationOutcome.Ambiguous;
    }
}
