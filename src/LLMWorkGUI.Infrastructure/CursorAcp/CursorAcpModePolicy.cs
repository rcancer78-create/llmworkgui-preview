using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Fail-closed Cursor ACP mode policy (ADR-0003 §5, §10). It computes sendability and the
/// writer-lock requirement itself from the discovered state and declared access; it never
/// delegates lock semantics to <c>CheckoutLockService</c>. Modes with a state other than
/// Supported cannot be selected or sent, and no writer-lock exemption is granted unless the mode
/// is both Supported and declared read-only.
/// </summary>
public sealed class CursorAcpModePolicy : ICursorAcpModePolicy
{
    public const string AskModeId = "ask";

    public const string PlanModeId = "plan";

    public const string AgentModeId = "agent";

    public const string UnknownModeId = "Unknown";

    public CursorAcpMode ParseModeId(string modeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);

        return modeId switch
        {
            AskModeId => CursorAcpMode.Ask,
            PlanModeId => CursorAcpMode.Plan,
            AgentModeId => CursorAcpMode.Agent,
            _ => CursorAcpMode.Unknown
        };
    }

    public CursorAcpModeAccess ParseAccess(string access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(access);

        return access switch
        {
            "read-only" => CursorAcpModeAccess.ReadOnly,
            "write" => CursorAcpModeAccess.Write,
            _ => CursorAcpModeAccess.Unknown
        };
    }

    public CursorAcpModeDecision Evaluate(
        CursorAcpMode mode,
        CursorAcpModeAccess access,
        CapabilityState state)
    {
        var modeId = GetModeId(mode);
        var isSupported = state == CapabilityState.Supported;
        var isReadOnly = access == CursorAcpModeAccess.ReadOnly;
        var canSend = mode != CursorAcpMode.Unknown && isSupported;
        var requiresWriterLock = mode switch
        {
            CursorAcpMode.Unknown => true,
            CursorAcpMode.Agent => true,
            _ => !(isSupported && isReadOnly)
        };

        return new CursorAcpModeDecision
        {
            Mode = mode,
            ModeId = modeId,
            Access = access,
            State = state,
            CanSend = canSend,
            RequiresWriterLock = requiresWriterLock,
            Blocker = canSend ? null : BuildBlocker(mode, modeId, state)
        };
    }

    /// <summary>Strict wire identifier of a mode: <c>ask</c>, <c>plan</c>, <c>agent</c> or <c>Unknown</c>.</summary>
    public static string GetModeId(CursorAcpMode mode) => mode switch
    {
        CursorAcpMode.Ask => AskModeId,
        CursorAcpMode.Plan => PlanModeId,
        CursorAcpMode.Agent => AgentModeId,
        _ => UnknownModeId
    };

    private static string BuildBlocker(CursorAcpMode mode, string modeId, CapabilityState state)
    {
        if (mode == CursorAcpMode.Unknown)
        {
            return $"The mode '{modeId}' is not one of 'ask', 'plan' or 'agent'; unknown modes cannot be " +
                   "selected or sent.";
        }

        return $"The mode '{modeId}' has capability state '{state}' instead of Supported; the mode cannot " +
               "be selected or sent (ADR-0003 §5.2) and no writer-lock exemption is granted.";
    }
}
