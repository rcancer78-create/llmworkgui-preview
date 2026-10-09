using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Fail-closed decision for a Cursor ACP mode: whether it can be selected or sent and whether the
/// checkout writer lock is required. The decision is computed by <see cref="ICursorAcpModePolicy"/>
/// from the discovered state and declared access; lock semantics are never delegated to
/// <c>CheckoutLockService</c> (ADR-0003 §5, §10).
/// </summary>
public sealed record CursorAcpModeDecision
{
    /// <summary>Parsed mode value.</summary>
    public required CursorAcpMode Mode { get; init; }

    /// <summary>Strict wire identifier: <c>ask</c>, <c>plan</c>, <c>agent</c> or <c>Unknown</c>.</summary>
    public required string ModeId { get; init; }

    /// <summary>Declared access of the mode.</summary>
    public required CursorAcpModeAccess Access { get; init; }

    /// <summary>Discovered capability state of the mode.</summary>
    public required CapabilityState State { get; init; }

    /// <summary>False unless the mode is known and its capability state is Supported.</summary>
    public required bool CanSend { get; init; }

    /// <summary>
    /// True unless read-only nature is proven by both a Supported state and a declared read-only
    /// access. The write mode <c>agent</c> and every Unknown mode always require the lock.
    /// </summary>
    public required bool RequiresWriterLock { get; init; }

    /// <summary>Exact reason the mode cannot be sent; null when <see cref="CanSend"/> is true.</summary>
    public string? Blocker { get; init; }
}
