using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Fail-closed mode policy of the Cursor ACP backend (ADR-0003 §5, §10). Modes whose capability
/// state is not Supported cannot be selected or sent, and no writer-lock exemption is granted
/// without a proven Supported read-only mode. The policy computes this invariant itself from the
/// passed state and access.
/// </summary>
public interface ICursorAcpModePolicy
{
    /// <summary>
    /// Maps a mode identifier strictly to <c>ask</c>/<c>plan</c>/<c>agent</c>; any other value is
    /// <see cref="CursorAcpMode.Unknown"/>.
    /// </summary>
    CursorAcpMode ParseModeId(string modeId);

    /// <summary>
    /// Maps an access identifier strictly to <c>read-only</c>/<c>write</c>; any other value is
    /// <see cref="CursorAcpModeAccess.Unknown"/> and is never treated as read-only.
    /// </summary>
    CursorAcpModeAccess ParseAccess(string access);

    /// <summary>Evaluates sendability and the writer-lock invariant for a mode, access and state.</summary>
    CursorAcpModeDecision Evaluate(CursorAcpMode mode, CursorAcpModeAccess access, CapabilityState state);
}
