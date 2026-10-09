using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Creates a protocol client over a transport bound to one managed <c>cursor-agent acp</c> process.
/// The client is stateful per process, so it is never a DI singleton (ADR-0003 §1).
/// </summary>
public interface ICursorAcpClientFactory
{
    ICursorAcpClient Create(IJsonRpcTransport transport);
}
