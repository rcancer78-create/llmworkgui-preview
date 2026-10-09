namespace LLMWorkGUI.Application.Reconciliation;

public sealed record ReconciliationEvidence
{
    public required string LocalSessionId { get; init; }

    public string? ExecutionId { get; init; }

    public string? NativeSessionId { get; init; }

    public required ReconciliationOutcome Outcome { get; init; }

    public required string Details { get; init; }

    public string EvidenceDetails => Details;

    public required bool ProcessAlive { get; init; }

    public required bool BindingMatched { get; init; }

    public required DateTimeOffset ReconciledAtUtc { get; init; }
}
