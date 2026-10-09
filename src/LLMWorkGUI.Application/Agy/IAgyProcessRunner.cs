namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Runs the standard agy CLI directly with a bounded timeout and machine-readable
/// stream-json output, confirming init/conversation/model/permissions/tool events and
/// the terminal result (ТЗ §6.11a). Cancellation and timeout terminate the whole
/// process tree; no hidden OpenCode plugin fallback exists.
/// </summary>
public interface IAgyProcessRunner
{
    Task<AgyRunResult> RunAsync(AgyRunRequest request, CancellationToken cancellationToken = default);
}
