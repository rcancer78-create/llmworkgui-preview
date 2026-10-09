using CloseReason = LLMWorkGUI.Domain.Enums.CloseReason;
using SessionState = LLMWorkGUI.Domain.Enums.SessionState;

namespace LLMWorkGUI.Application.Reconciliation;

public sealed record ReconciliationRecoveryResult
{
    public required string LocalSessionId { get; init; }

    public required RecoveryAction Action { get; init; }

    public required SessionState SessionState { get; init; }

    public required CloseReason CloseReason { get; init; }

    public string? ReleasedLockId { get; init; }

    public required string AuditDetails { get; init; }
}
