using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Agy;

public sealed class OpenCodePluginFilteringTests
{
    private readonly OpenCodePluginInventoryService _service = new();

    [Fact]
    public void GetInstalledPlugins_ExcludesEveryPluginMentioningAgyAntigravityOrCodex()
    {
        const string jsonc = """
        {
          "plugin": [
            "opencode-plugin-git",
            "opencode-agy-account-switcher",
            "codex-profile-pinner",
            "opencode-antigravity-profile-rotator",
            "agy-provider",
            "codex-plugin",
            "opencode-plugin-review"
          ]
        }
        """;

        var plugins = _service.GetInstalledPlugins(jsonc);

        Assert.Equal(2, plugins.Count);
        Assert.Contains(plugins, plugin => plugin.Name == "opencode-plugin-git");
        Assert.Contains(plugins, plugin => plugin.Name == "opencode-plugin-review");
        Assert.DoesNotContain(plugins, plugin => plugin.Name.Contains("agy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plugins, plugin => plugin.Name.Contains("codex", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plugins, plugin => plugin.Name.Contains("antigravity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetInstalledPlugins_ExcludesProviderOnlyNamesWithoutAccountSwitchingWording()
    {
        const string jsonc = """
        {
          "plugin": {
            "agy-provider": true,
            "codex-plugin": true,
            "antigravity-theme": true,
            "codex-linter": true
          }
        }
        """;

        Assert.Empty(_service.GetInstalledPlugins(jsonc));

        var excluded = _service.GetExcludedManagedRoutePlugins(jsonc);

        Assert.Equal(4, excluded.Count);
    }

    [Fact]
    public void GetInstalledPlugins_KeepsPluginsUnrelatedToTheAgyCodexBoundary()
    {
        const string jsonc = """
        {
          "plugin": {
            "gemini-formatter": true,
            "opencode-plugin-git": true
          }
        }
        """;

        var plugins = _service.GetInstalledPlugins(jsonc);

        Assert.Equal(2, plugins.Count);
    }

    [Fact]
    public void GetInstalledPlugins_UsesDescriptionToClassifySwitchingPlugins()
    {
        const string jsonc = """
        {
          "plugin": [
            {
              "name": "custom-plugin",
              "description": "Switches between Codex accounts from OpenCode"
            }
          ]
        }
        """;

        Assert.Empty(_service.GetInstalledPlugins(jsonc));

        var excluded = _service.GetExcludedManagedRoutePlugins(jsonc);
        var plugin = Assert.Single(excluded);
        Assert.Equal("custom-plugin", plugin.Name);
    }

    [Fact]
    public void GetExcludedManagedRoutePlugins_MarksExcludedPluginsForDiagnostics()
    {
        const string jsonc = """
        {
          "plugin": {
            "opencode-agy-account-switcher": true,
            "opencode-plugin-git": true
          }
        }
        """;

        var excluded = _service.GetExcludedManagedRoutePlugins(jsonc);

        var plugin = Assert.Single(excluded);
        Assert.Equal("opencode-agy-account-switcher", plugin.Name);
        Assert.True(plugin.Enabled);
    }

    [Theory]
    [InlineData("opencode-agy-account-switcher", null, true)]
    [InlineData("codex-profile-pinner", null, true)]
    [InlineData("plugin", "rotates AGY credentials", true)]
    [InlineData("agy-provider", null, true)]
    [InlineData("codex-plugin", null, true)]
    [InlineData("opencode-antigravity-profile-rotator", null, true)]
    [InlineData("codex-linter", null, true)]
    [InlineData("antigravity-theme", null, true)]
    [InlineData("plugin", "mentions Antigravity in the description", true)]
    [InlineData("gemini-formatter", null, false)]
    [InlineData("opencode-plugin-git", "git integration", false)]
    [InlineData("account-switcher", null, false)]
    public void IsAgyOrCodexAccountSwitchingPlugin_ClassifiesPlugins(
        string name,
        string? description,
        bool expected)
    {
        var plugin = new PluginInfo(name, Description: description);

        Assert.Equal(expected, OpenCodePluginInventoryService.IsAgyOrCodexAccountSwitchingPlugin(plugin));
    }

    [Fact]
    public async Task LoadPluginsFromConfigFileAsync_AppliesManagedRouteFiltering()
    {
        using var directory = new TestDirectory();
        var configPath = directory.GetPath("opencode.jsonc");

        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "plugin": [
                "opencode-agy-account-switcher",
                "opencode-plugin-git"
              ]
            }
            """);

        var plugins = await _service.LoadPluginsFromConfigFileAsync(configPath);

        var plugin = Assert.Single(plugins);
        Assert.Equal("opencode-plugin-git", plugin.Name);
    }
}
