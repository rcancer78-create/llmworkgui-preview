using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class GatewayNativeExitTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(7, true)]
    public async Task PartialTextDoesNotHideNonzeroNativeExit(int exitCode, bool expectError)
    {
        using var directory = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<NativeChatEvent>();
        await foreach (var item in new ProcessFixtureAdapter().ReadAsync(exitCode, directory.Root, timeout.Token)) events.Add(item);
        Assert.Contains(events, e => e.Kind == NativeChatEventKind.Text && e.Text == "partial");
        Assert.Equal(expectError, events.Any(e => e.Kind == NativeChatEventKind.Error));
    }

    [Fact]
    public async Task ParserDiagnosticsExcludeRawOutputAndExceptionPayload()
    {
        using var directory = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new GatewayLogCapture<NativeAdapterBase>();
        await foreach (var item in new ProcessFixtureAdapter(logger, true).ReadAsync(0, directory.Root, timeout.Token)) { }
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("partial", entry.Text);
        Assert.DoesNotContain("parser-private-fixture", entry.Text);
        Assert.Contains("InvalidOperationException", entry.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountStoreDiagnosticsExcludeConfigurationPath(bool saveFailure)
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("private-config-fixture");
        if (saveFailure) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "{ malformed synthetic configuration");
        var logger = new GatewayLogCapture<JsonAccountStore>();
        var options = new GatewayOptions { AccountsFile = path, DiscoverProfiles = false };
        if (saveFailure) Assert.Throws<GatewayException>(() => JsonAccountStore.Load(options, [], logger));
        else JsonAccountStore.Load(options, [], logger);
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("private-config-fixture", entry.Text);
        Assert.DoesNotContain(directory.Root, entry.Text);
    }

    [Fact]
    public void ProfileDiscoveryDiagnosticsExcludeExceptionPayload()
    {
        using var directory = new TestDirectory();
        var logger = new GatewayLogCapture<JsonAccountStore>();
        JsonAccountStore.Load(new GatewayOptions { AccountsFile = directory.GetPath("profiles.json"), DiscoverProfiles = true },
            [new ProcessFixtureAdapter(failDiscovery: true)], logger);
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("discovery-private-fixture", entry.Text);
        Assert.Contains("IOException", entry.Text);
    }

    // Runs a deterministic local process fixture, never a native LLM CLI or network request.
    private sealed class ProcessFixtureAdapter(ILogger? logger = null, bool failParser = false, bool failDiscovery = false)
        : NativeAdapterBase(new ExecutableResolver(), new GatewayOptions(), logger ?? NullLogger.Instance)
    {
        public IAsyncEnumerable<NativeChatEvent> ReadAsync(int exitCode, string root, CancellationToken token)
        {
            var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            File.WriteAllText(Path.Combine(root, "fixture.cmd"), "@echo off\r\necho {\"text\":\"partial\"}\r\nexit /b " + exitCode + "\r\n");
            var launch = new NativeLaunch(new(shell, [], LaunchKind.Direct, shell),
                ["/d", "/c", "fixture.cmd"],
                new Dictionary<string, string?>(), root);
            return StreamAsync(launch, new Parser(failParser), token);
        }
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Fixture";
        public override string DefaultExecutable => "fixture";
        public override IEnumerable<AccountProfile> DiscoverProfiles() => failDiscovery ? throw new IOException("discovery-private-fixture") : [];
        public override ProviderCapabilities Capabilities => new(false, "fixture", MultiAccountSupport.Isolated, "fixture", null, null, false, 100);
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => throw new NotSupportedException();
        private sealed class Parser(bool fail) : IChatLineParser
        {
            public IEnumerable<NativeChatEvent> Parse(JsonElement line) => fail ? throw new InvalidOperationException("parser-private-fixture") : [NativeChatEvent.Delta(line.GetProperty("text").GetString()!)];
        }
    }
}

internal sealed class GatewayLogCapture<T> : ILogger<T>
{
    public List<(string Text, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((formatter(state, exception), exception));
}
