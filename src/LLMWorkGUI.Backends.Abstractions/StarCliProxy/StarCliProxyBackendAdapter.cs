namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Backend adapter identity for the star-cliproxy gateway. Codex and AGY are routed exclusively
/// through this backend; the OpenCode adapter never carries Codex/AGY traffic (ADR-0007, ТЗ §6.4, §6.11a).
/// </summary>
public sealed class StarCliProxyBackendAdapter : IBackendAdapter
{
    public const string StarCliProxyBackendId = "star-cliproxy";

    public string BackendId => StarCliProxyBackendId;
}
