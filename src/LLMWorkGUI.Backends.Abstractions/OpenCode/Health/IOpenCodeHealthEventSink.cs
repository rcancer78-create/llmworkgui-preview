namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Health;

public interface IOpenCodeHealthEventSink
{
    Task RecordHealthEventAsync(
        string entityId,
        string failureReason,
        Exception? exception = null,
        CancellationToken cancellationToken = default);
}
