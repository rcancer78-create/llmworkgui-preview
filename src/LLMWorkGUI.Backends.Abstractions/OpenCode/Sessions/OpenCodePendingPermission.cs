using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

/// <summary>Immutable, redacted snapshot of one request in the current native turn.</summary>
public sealed record OpenCodePendingPermission
{
    public required string ReceiptId { get; init; }
    public required string RequestId { get; init; }
    public required string SessionId { get; init; }
    public required string NativeKind { get; init; }
    public required string OriginalRequestDisplay { get; init; }
    public required string Explanation { get; init; }
    public NormalizedApprovalKind Kind { get; init; }
    public bool CanAllowOnce { get; init; }
    public bool CanDeny { get; init; }
    public bool ReplyAttempted { get; init; }
    public bool HasConflictingRequest { get; init; }
    public bool IsDisplayTruncated { get; init; }

    // Native "always" approves patterns until the session ends, which exceeds one execution.
    public bool CanAllowForExecution => false;
    public string Display => NativeKind + " · " + ReceiptId[..Math.Min(8, ReceiptId.Length)];
}
