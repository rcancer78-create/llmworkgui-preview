using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class PluginInventoryServiceTests
{
    private readonly OpenCodePluginInventoryService _service = new();

    [Fact]
    public void GetInstalledPlugins_StringArrayFormat_ParsesAllPluginNames()
    {
        var jsonc = """
        {
          "$schema": "https://opencode.ai/config.json",
          // List of installed plugins
          "plugin": [
            "opencode-plugin-git",
            "opencode-plugin-review",
            "opencode-plugin-terminal"
          ]
        }
        """;

        var plugins = _service.GetInstalledPlugins(jsonc);

        Assert.Equal(3, plugins.Count);
        Assert.Contains(plugins, p => p.Name == "opencode-plugin-git" && p.Enabled);
        Assert.Contains(plugins, p => p.Name == "opencode-plugin-review" && p.Enabled);
        Assert.Contains(plugins, p => p.Name == "opencode-plugin-terminal" && p.Enabled);
    }

    [Fact]
    public void GetInstalledPlugins_ObjectDictionaryFormat_ParsesEnabledFlags()
    {
        var jsonc = """
        {
          "plugin": {
            "plugin-enabled": true,
            "plugin-disabled": false,
            "plugin-detailed": {
              "version": "2.1.0",
              "enabled": true,
              "description": "A detailed plugin"
            }
          }
        }
        """;

        var plugins = _service.GetInstalledPlugins(jsonc);

        Assert.Equal(3, plugins.Count);

        var enabledPlugin = plugins.First(p => p.Name == "plugin-enabled");
        Assert.True(enabledPlugin.Enabled);

        var disabledPlugin = plugins.First(p => p.Name == "plugin-disabled");
        Assert.False(disabledPlugin.Enabled);

        var detailedPlugin = plugins.First(p => p.Name == "plugin-detailed");
        Assert.True(detailedPlugin.Enabled);
        Assert.Equal("2.1.0", detailedPlugin.Version);
        Assert.Equal("A detailed plugin", detailedPlugin.Description);
    }

    [Fact]
    public void GetInstalledPlugins_MissingOrEmptyPluginSection_ReturnsEmptyList()
    {
        Assert.Empty(_service.GetInstalledPlugins(null));
        Assert.Empty(_service.GetInstalledPlugins(""));
        Assert.Empty(_service.GetInstalledPlugins("{}"));
        Assert.Empty(_service.GetInstalledPlugins("{ \"provider\": {} }"));
    }
}
