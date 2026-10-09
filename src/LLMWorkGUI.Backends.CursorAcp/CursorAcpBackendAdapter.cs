using LLMWorkGUI.Backends.Abstractions;

namespace LLMWorkGUI.Backends.CursorAcp;

public sealed class CursorAcpBackendAdapter : IBackendAdapter
{
    public string BackendId => "cursor-acp";
}
