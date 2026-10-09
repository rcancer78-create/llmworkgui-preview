namespace LLMWorkGUI.Domain.Enums;

public enum HealthState
{
    Healthy,
    Degraded,
    CoolingDown,
    QuarantinedAuto,
    DisabledManual,
    ProbeRequired,
    Recovering,
    ForcedEnabled
}
