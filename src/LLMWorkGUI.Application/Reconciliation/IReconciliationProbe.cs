namespace LLMWorkGUI.Application.Reconciliation;

public interface IReconciliationProbe
{
    Task<ReconciliationProbeResult> ProbeAsync(
        ReconciliationProbeRequest request,
        CancellationToken cancellationToken = default);
}
