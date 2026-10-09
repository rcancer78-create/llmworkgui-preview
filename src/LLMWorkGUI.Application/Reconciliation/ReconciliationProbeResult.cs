using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Reconciliation;

public sealed record ReconciliationProbeResult
{
    public required bool BackendAvailable { get; init; }

    public required bool ProcessAlive { get; init; }

    public string? ObservedNativeSessionId { get; init; }

    public SessionBinding? ObservedBinding { get; init; }

    public required string Details { get; init; }
}
