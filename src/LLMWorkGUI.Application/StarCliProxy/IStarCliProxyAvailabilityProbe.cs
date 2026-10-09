namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Reports whether the star-cliproxy gateway integration is usable. Application-layer bridges use
/// this to keep Codex/AGY routes fail-closed in degraded mode (ADR-0007, ТЗ §6.11a).
/// </summary>
public interface IStarCliProxyAvailabilityProbe
{
    bool IsAvailable { get; }

    string? AvailabilityBlocker { get; }
}
