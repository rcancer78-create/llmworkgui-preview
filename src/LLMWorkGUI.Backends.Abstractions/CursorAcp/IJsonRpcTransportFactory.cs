namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Creates a JSON-RPC transport over a pair of agent stdio streams. The read stream carries the
/// agent standard output; the write stream receives the agent standard input.
/// </summary>
public interface IJsonRpcTransportFactory
{
    IJsonRpcTransport Create(Stream agentStandardOutput, Stream agentStandardInput);
}
