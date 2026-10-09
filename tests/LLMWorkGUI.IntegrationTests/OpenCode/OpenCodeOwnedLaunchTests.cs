using System.Net;
using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeOwnedLaunchTests
{
    private readonly ITestOutputHelper _output;

    public OpenCodeOwnedLaunchTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("", "KEY", "value")]
    [InlineData("outside/path", "KEY", "value")]
    [InlineData("owned-safe", "BAD=KEY", "value")]
    [InlineData("owned-safe", "BAD\nKEY", "value")]
    [InlineData("owned-safe", "KEY", "bad\0value")]
    public void InvalidOwnedLaunchIdentityOrEnvironmentIsRefusedBeforeExecution(string id, string key, string value)
    {
        using var files = new TestDirectory();
        Assert.Throws<ArgumentException>(() => new OpenCodeOwnedServerLaunch(id, files.Root,
            new Dictionary<string, string> { [key] = value }));
    }

    [Fact]
    public void CaseDuplicateEnvironmentKeysAreRefusedAndCallerCannotModifySnapshot()
    {
        using var files = new TestDirectory();
        Assert.Throws<ArgumentException>(() => new OpenCodeOwnedServerLaunch("owned-safe", files.Root,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["KEY"] = "one", ["key"] = "two" }));
        var launch = new OpenCodeOwnedServerLaunch("owned-safe", files.Root,
            new Dictionary<string, string> { ["KEY"] = "synthetic" });
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)launch.EnvironmentVariables).Add("OTHER", "value"));
    }

    [Fact]
    public void OwnedServerCannotUseFixedPortOrExecutableDiscovery()
    {
        using var files = new TestDirectory(); using var logs = new TestDirectory();
        var launch = new OpenCodeOwnedServerLaunch("owned-safe", files.Root, new Dictionary<string, string>());
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = logs.Root }); using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath = typeof(OpenCodeOwnedLaunchTests).Assembly.Location, Port = 12345 }),
            supervisor, new NoDiscovery(), http, ownedLaunch: launch));
        Assert.Throws<ArgumentException>(() => new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions()),
            supervisor, new NoDiscovery(), http, ownedLaunch: launch));
    }
    private sealed class NoDiscovery : IOpenCodeDiscoveryService
    {
        public Task<OpenCodeDiscoveryResult> DiscoverAsync(CancellationToken token = default)
            => throw new InvalidOperationException("Owned executable must not discover another CLI.");
    }
    private sealed class NoPid : IProcessIdResolver
    { public int? ResolveChildProcessId(string path, DateTimeOffset time) => null; }

    [Fact]
    public async Task ActualOwnedServerUsesExactIdentityDirectoryAndOnlyExplicitEnvironment()
    {
        using var files = new TestDirectory(); using var logs = new TestDirectory();
        var ambient = "LLMWORKGUI_AMBIENT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(ambient, "synthetic-parent-only");
        var capture = files.GetPath("capture.json");
        var script = files.GetPath("server.ps1");
        var cli = files.GetPath("opencode.cmd");
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        await File.WriteAllTextAsync(script, $$"""
            $ErrorActionPreference='Stop'
            $value=@{Ambient=[Environment]::GetEnvironmentVariable('{{ambient}}');Selected=$env:LLMWORKGUI_SELECTED;
                Directory=[Environment]::CurrentDirectory;Home=$env:HOME}
            [IO.File]::WriteAllText('{{capture.Replace("'", "''", StringComparison.Ordinal)}}',($value|ConvertTo-Json -Compress))
            Write-Output 'Listening on http://127.0.0.1:54321'
            Start-Sleep -Seconds 3600
            """);
        await File.WriteAllTextAsync(cli, $"@echo off\r\n\"{powerShell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"\r\n");
        var environment = new Dictionary<string, string>
        {
            ["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory)!,
            ["HOME"] = files.Root, ["LLMWORKGUI_SELECTED"] = "synthetic-selected"
        };
        var id = "owned-adaptation-" + Guid.NewGuid().ToString("N");
        var launch = new OpenCodeOwnedServerLaunch(id, files.Root, environment);
        environment["LLMWORKGUI_SELECTED"] = "mutated-after-snapshot";
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = logs.Root });
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath = cli, StartupTimeout = TimeSpan.FromSeconds(10) }), supervisor,
            new NoDiscovery(), http, new NoPid(), ownedLaunch: launch);
        IOpenCodeServerInstance? instance = null;
        try
        {
            instance = await manager.StartServerAsync();
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
            Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("Ambient").ValueKind);
            Assert.Equal("synthetic-selected", data.RootElement.GetProperty("Selected").GetString());
            Assert.Equal(files.Root, data.RootElement.GetProperty("Directory").GetString());
            Assert.Equal(files.Root, data.RootElement.GetProperty("Home").GetString());
            Assert.Equal(id, instance.InstanceId);
        }
        catch
        {
            // Startup cleanup has its own deadline and can mask the original
            // failure. Preserve the owned console fixture's output in the TRX.
            _output.WriteLine($"Owned fixture capture created: {File.Exists(capture)}");
            foreach (var path in Directory.EnumerateFiles(logs.Root, "*.log", SearchOption.AllDirectories))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    _output.WriteLine($"{Path.GetFileName(path)}: {text[..Math.Min(text.Length, 4096)]}");
                }
                catch (IOException error)
                {
                    _output.WriteLine($"{Path.GetFileName(path)} could not be read: {error.GetType().Name}");
                }
            }
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ambient, null);
            if (instance is not null) await manager.StopServerAsync(instance);
        }
        Assert.True(instance!.IsTerminationConfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartServerAsync());
    }
}
