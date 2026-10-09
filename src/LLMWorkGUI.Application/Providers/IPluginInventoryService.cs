namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Service for inspecting and querying installed OpenCode plugins.
/// </summary>
public interface IPluginInventoryService
{
    IReadOnlyList<PluginInfo> GetInstalledPlugins(string? opencodeConfigContent);
    Task<IReadOnlyList<PluginInfo>> LoadPluginsFromConfigFileAsync(string configFilePath, CancellationToken cancellationToken = default);
}
