namespace LLMWorkGUI.Application.Workflows;

/// <summary>Actual serialized HTTP boundary and durable uncertain ownership, not native identity/termination proof.</summary>
public interface IAdaptationTransportPolicy
{
    Task AuthorizeCreateAsync(Guid admissionId, Uri requestUri, ReadOnlyMemory<byte> body, CancellationToken token);
    Task BindCreatedAsync(Guid admissionId, string nativeSessionId, string? reportedDirectory, Uri requestUri, CancellationToken token);
    Task AuthorizePromptAsync(Guid? admissionId, string nativeSessionId, Uri requestUri, ReadOnlyMemory<byte> body, CancellationToken token);
    Task AuthorizeAbortAsync(string nativeSessionId, Uri requestUri, CancellationToken token);
}
