namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Inspects whether a live <c>agy</c> process exists before any profile switch is attempted
/// (ТЗ §6.4, §6.11a: switching while agy is running is forbidden).
/// </summary>
public interface IAgyProcessInspector
{
    Task<bool> IsAgyRunningAsync(CancellationToken cancellationToken = default);
}
