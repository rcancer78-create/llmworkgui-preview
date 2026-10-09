using LLMWorkGUI.Backends.Abstractions;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed class OpenCodeBackendAdapter : IBackendAdapter
{
    public string BackendId => "opencode";
}
