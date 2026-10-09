namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public interface IOpenCodeSseParser
{
    IAsyncEnumerable<OpenCodeEventEnvelope> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OpenCodeEventEnvelope> ParseAsync(
        TextReader reader,
        CancellationToken cancellationToken = default);
}
