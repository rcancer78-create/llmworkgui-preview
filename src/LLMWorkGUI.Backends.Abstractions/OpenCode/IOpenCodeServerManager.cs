namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public interface IOpenCodeServerManager
{
    Task<IOpenCodeServerInstance> StartServerAsync(CancellationToken cancellationToken = default);

    Task StopServerAsync(IOpenCodeServerInstance instance, CancellationToken cancellationToken = default);
}
