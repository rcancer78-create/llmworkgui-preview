using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Input of one supervised Cursor ACP turn. It carries the prompt, the fail-closed mode decision and
/// the checkout writer lock token required by that decision (ADR-0003 §5, §10, ТЗ §6.7).
/// </summary>
public sealed record CursorAcpTurnRequest
{
    /// <summary>Prompt payload including the execution identity and the validated model string.</summary>
    public required CursorAcpPromptRequest Prompt { get; init; }

    /// <summary>
    /// Mode decision produced by <see cref="ICursorAcpModePolicy"/>. A decision with
    /// <c>CanSend == false</c> refuses the turn before any prompt is dispatched.
    /// </summary>
    public required CursorAcpModeDecision Mode { get; init; }

    /// <summary>
    /// Active writer lock token. It is mandatory when <see cref="CursorAcpModeDecision.RequiresWriterLock"/>
    /// is true and the turn is refused without it.
    /// </summary>
    public ICheckoutLockToken? LockToken { get; init; }
}
