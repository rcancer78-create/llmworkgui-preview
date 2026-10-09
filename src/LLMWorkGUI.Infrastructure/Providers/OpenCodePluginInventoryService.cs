using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Service for inspecting and discovering installed OpenCode plugins from configuration.
/// Any plugin that mentions AGY/Antigravity/Codex in its name or description is filtered out
/// fail-closed and never loaded for application-managed routes (ТЗ §6.11); excluded plugins
/// are reported separately for diagnostics.
/// </summary>
public sealed class OpenCodePluginInventoryService : IPluginInventoryService
{
    private static readonly string[] AgyCodexProviderTokens = ["agy", "antigravity", "codex"];

    public IReadOnlyList<PluginInfo> GetInstalledPlugins(string? opencodeConfigContent)
    {
        return ParsePlugins(opencodeConfigContent)
            .Where(plugin => !IsAgyOrCodexAccountSwitchingPlugin(plugin))
            .ToArray();
    }

    /// <summary>
    /// Returns the plugins that were excluded from <see cref="GetInstalledPlugins"/> because
    /// they participate in AGY/Codex account or profile switching (ТЗ §6.11). These plugins
    /// stay marked for diagnostics and are never loaded for managed routes.
    /// </summary>
    public IReadOnlyList<PluginInfo> GetExcludedManagedRoutePlugins(string? opencodeConfigContent)
    {
        return ParsePlugins(opencodeConfigContent)
            .Where(IsAgyOrCodexAccountSwitchingPlugin)
            .ToArray();
    }

    /// <summary>
    /// Classifies an OpenCode plugin as an AGY/Codex plugin. The filter is fail-closed: any
    /// plugin whose name or description mentions <c>agy</c>, <c>antigravity</c> or <c>codex</c>
    /// is excluded from managed routes regardless of any additional account-switching wording
    /// (ТЗ §6.11).
    /// </summary>
    public static bool IsAgyOrCodexAccountSwitchingPlugin(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var text = string.Concat(plugin.Name, " ", plugin.Description ?? string.Empty).ToLowerInvariant();

        return AgyCodexProviderTokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<PluginInfo>> LoadPluginsFromConfigFileAsync(string configFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(configFilePath))
        {
            return Array.Empty<PluginInfo>();
        }

        try
        {
            var content = await File.ReadAllTextAsync(configFilePath, cancellationToken).ConfigureAwait(false);
            return GetInstalledPlugins(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<PluginInfo>();
        }
    }

    private static IReadOnlyList<PluginInfo> ParsePlugins(string? opencodeConfigContent)
    {
        if (string.IsNullOrWhiteSpace(opencodeConfigContent))
        {
            return Array.Empty<PluginInfo>();
        }

        var plugins = new List<PluginInfo>();
        try
        {
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var node = JsonNode.Parse(opencodeConfigContent, documentOptions: options);
            if (node == null) return plugins;

            var pluginNode = node["plugin"];
            if (pluginNode == null) return plugins;

            // Case 1: "plugin": [ "plugin-a", "plugin-b" ] or [ { "name": "plugin-a", ... } ]
            if (pluginNode is JsonArray pluginArray)
            {
                foreach (var item in pluginArray)
                {
                    if (item is JsonValue val && val.TryGetValue<string>(out var nameStr) && !string.IsNullOrWhiteSpace(nameStr))
                    {
                        plugins.Add(new PluginInfo(nameStr.Trim()));
                    }
                    else if (item is JsonObject obj)
                    {
                        var name = obj["name"]?.GetValue<string>() ?? obj["id"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var version = obj["version"]?.GetValue<string>();
                            var enabled = obj["enabled"]?.GetValue<bool>() ?? true;
                            var desc = obj["description"]?.GetValue<string>();
                            plugins.Add(new PluginInfo(name.Trim(), version, enabled, desc));
                        }
                    }
                }
            }
            // Case 2: "plugin": { "plugin-a": true, "plugin-b": false } or { "plugin-a": { ... } }
            else if (pluginNode is JsonObject pluginObj)
            {
                foreach (var (propName, propVal) in pluginObj)
                {
                    if (string.IsNullOrWhiteSpace(propName)) continue;

                    if (propVal is JsonValue bVal && bVal.TryGetValue<bool>(out var isEnabled))
                    {
                        plugins.Add(new PluginInfo(propName.Trim(), Enabled: isEnabled));
                    }
                    else if (propVal is JsonObject detailObj)
                    {
                        var version = detailObj["version"]?.GetValue<string>();
                        var enabled = detailObj["enabled"]?.GetValue<bool>() ?? true;
                        var desc = detailObj["description"]?.GetValue<string>();
                        plugins.Add(new PluginInfo(propName.Trim(), version, enabled, desc));
                    }
                    else
                    {
                        plugins.Add(new PluginInfo(propName.Trim()));
                    }
                }
            }
        }
        catch
        {
            // Non-fatal if invalid JSONC
        }

        return plugins;
    }
}
