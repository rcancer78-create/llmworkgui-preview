using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class OpenCodeAdaptationRuntimeTests
{
    [Theory]
    [InlineData("home")]
    [InlineData("directory")]
    [InlineData("config")]
    [InlineData("state")]
    public void ForeignOrRelativeRuntimePathsAreRefused(string field)
    {
        using var files = new TestDirectory();
        var paths = new Dictionary<string, string>
        {
            ["home"] = files.Root, ["directory"] = Path.Combine(files.Root, "workspace"),
            ["config"] = Path.Combine(files.Root, "config", "opencode"),
            ["state"] = Path.Combine(files.Root, "state", "opencode")
        };
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(paths)))
            Assert.True(OpenCodeAdaptationRuntime.HasOwnedPaths(json.RootElement, files.Root));
        foreach (var replacement in new[] { Path.Combine(files.Root, "foreign"), "relative/path", "" })
        {
            paths[field] = replacement;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(paths));
            Assert.False(OpenCodeAdaptationRuntime.HasOwnedPaths(json.RootElement, files.Root));
        }
        paths.Remove(field);
        using var missing = JsonDocument.Parse(JsonSerializer.Serialize(paths));
        Assert.False(OpenCodeAdaptationRuntime.HasOwnedPaths(missing.RootElement, files.Root));
    }

    [Theory]
    [InlineData("share")]
    [InlineData("autoupdate")]
    [InlineData("snapshot")]
    [InlineData("lsp")]
    [InlineData("formatter")]
    [InlineData("instructions")]
    public void EnabledOrOmittedAuxiliaryServiceIsRefused(string field)
    {
        var values = new Dictionary<string, object>
        {
            ["share"] = "disabled", ["autoupdate"] = false, ["snapshot"] = false,
            ["lsp"] = false, ["formatter"] = false, ["instructions"] = Array.Empty<string>()
        };
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(values)))
            Assert.True(OpenCodeAdaptationRuntime.HasDisabledAuxiliaryServices(json.RootElement));
        values[field] = field == "share" ? "auto" : field == "instructions" ? new[] { "foreign.md" } : true;
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(values)))
            Assert.False(OpenCodeAdaptationRuntime.HasDisabledAuxiliaryServices(json.RootElement));
        values.Remove(field);
        using var missing = JsonDocument.Parse(JsonSerializer.Serialize(values));
        Assert.False(OpenCodeAdaptationRuntime.HasDisabledAuxiliaryServices(missing.RootElement));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("task")]
    [InlineData("skill")]
    [InlineData("webfetch")]
    [InlineData("*")]
    [InlineData("unrecognized_tool")]
    public void LateNativeToolAllowOrAskCannotOverrideGlobalDeny(string permission)
    {
        foreach (var action in new[] { "allow", "ask" })
        {
            using var rules = JsonDocument.Parse(JsonSerializer.Serialize(new[]
            {
                new { permission = "*", pattern = "*", action = "deny" },
                new { permission, pattern = "*", action }
            }));
            Assert.False(OpenCodeAdaptationRuntime.HasOnlyDeniedTools(rules.RootElement, "D:/owned/output/*"));
        }
    }

    [Theory]
    [InlineData("D:/foreign/output/*")]
    [InlineData("D:/owned/*")]
    [InlineData("*")]
    [InlineData("D:/owned/output/../outside/*")]
    public void ForeignOrExpandedNativeOutputDirectoryAllowIsRefused(string pattern)
    {
        using var rules = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { permission = "*", pattern = "*", action = "deny" },
            new { permission = "external_directory", pattern, action = "allow" }
        }));
        Assert.False(OpenCodeAdaptationRuntime.HasOnlyDeniedTools(rules.RootElement, "D:/owned/output/*"));
    }

    [Fact]
    public void NativeOwnOutputDirectoryGuardDoesNotGrantAReadPermission()
    {
        using var rules = JsonDocument.Parse("""
            [{"permission":"read","pattern":"*","action":"allow"},
             {"permission":"*","pattern":"*","action":"deny"},
             {"permission":"external_directory","pattern":"D:/owned/output/*","action":"allow"}]
            """);
        Assert.True(OpenCodeAdaptationRuntime.HasOnlyDeniedTools(rules.RootElement, "D:/owned/output/*"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"permission\":\"read\",\"pattern\":\"*\",\"action\":\"deny\"}]")]
    [InlineData("[{\"permission\":\"*\",\"pattern\":\"*\",\"action\":null}]")]
    public void MissingOrMalformedNativeWildcardDenialIsRefused(string json)
    {
        using var rules = JsonDocument.Parse(json);
        Assert.False(OpenCodeAdaptationRuntime.HasOnlyDeniedTools(rules.RootElement, "D:/owned/output/*"));
    }

    [Fact]
    public async Task StoppedRuntimeCannotAdmitOrLaunchLater()
    {
        using var files = new TestDirectory();
        var id = Guid.NewGuid(); var root = Path.Combine(files.Root, "scratch", "adaptation-runtime", id.ToString("N"));
        Directory.CreateDirectory(root); var calls = 0;
        var runtime = new OpenCodeAdaptationRuntime("adaptation-native-" + id.ToString("N"),
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), root,
            "openai/synthetic-model", "synthetic-fixture-key", new StorageOptions { AppDataDirectory = files.Root },
            (_, _, _, _) => { calls++; return Task.CompletedTask; });
        await runtime.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
        Assert.Equal(0, calls); Assert.Null(runtime.BaseUrl);
        Assert.False(Directory.Exists(Path.Combine(files.Root, "runs")));
        await runtime.StopAsync();
    }

    [Fact]
    public async Task StopWaitsForTheSameAdmissionAndDoesNotStartAnotherAttempt()
    {
        using var files = new TestDirectory();
        var id = Guid.NewGuid(); var root = Path.Combine(files.Root, "scratch", "adaptation-runtime", id.ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new OpenCodeAdaptationRuntime("adaptation-native-" + id.ToString("N"),
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), root,
            "openai/synthetic-model", "synthetic-fixture-key", new StorageOptions { AppDataDirectory = files.Root },
            async (_, _, _, _) => { entered.SetResult(); await finish.Task; throw new IOException("synthetic admission failure"); });
        var start = runtime.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = runtime.StopAsync();
        try { Assert.False(stop.IsCompleted); }
        finally { finish.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => start);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(runtime.BaseUrl);
        Assert.False(Directory.Exists(Path.Combine(files.Root, "runs")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
    }

    [Fact]
    public async Task FailedAdmissionCannotLaunchAnyNativeProcessOrPublishAnEndpoint()
    {
        using var files = new TestDirectory();
        var id = Guid.NewGuid(); var root = Path.Combine(files.Root, "scratch", "adaptation-runtime", id.ToString("N"));
        Directory.CreateDirectory(root); var called = 0;
        var runtime = new OpenCodeAdaptationRuntime("adaptation-native-" + id.ToString("N"),
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), root,
            "openai/synthetic-model", "synthetic-fixture-key", new StorageOptions { AppDataDirectory = files.Root },
            (native, cwd, config, _) =>
            {
                Assert.Equal("adaptation-native-" + id.ToString("N"), native);
                Assert.Equal(Path.Combine(root, "workspace"), cwd); Assert.Equal(64, config.Length);
                called++; throw new IOException("synthetic failed durable audit");
            });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => runtime.StartAsync());
            Assert.Equal(1, called); Assert.Null(runtime.BaseUrl);
            Assert.False(Directory.Exists(Path.Combine(files.Root, "runs")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
        }
        finally { await runtime.StopAsync(); }
    }
}
