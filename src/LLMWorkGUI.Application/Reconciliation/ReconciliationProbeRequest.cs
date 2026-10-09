using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Reconciliation;

public sealed record ReconciliationProbeRequest
{
    public required Session Session { get; init; }

    public Execution? ActiveExecution { get; init; }

    public IReadOnlyList<ExecutionEventRecord> ExecutionEvents { get; init; } = Array.Empty<ExecutionEventRecord>();
}
