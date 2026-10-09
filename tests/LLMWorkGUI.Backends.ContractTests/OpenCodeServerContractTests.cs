using System.Text.Json;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeServerContractTests
{
    private const string SampleDoc = """
    {
      "openapi": "3.1.0",
      "info": { "title": "opencode", "version": "1.18.31" },
      "paths": {
        "/session": { "get": {}, "post": {} },
        "/instance/dispose": { "post": {} },
        "/doc": { "get": {} }
      }
    }
    """;

    [Fact]
    public void ServerOptions_Defaults_TargetLoopbackAndOsAssignedPort()
    {
        var options = new OpenCodeServerOptions();

        Assert.Equal("127.0.0.1", options.Hostname);
        Assert.Equal(0, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.DisposeTimeout);
        Assert.Null(options.CustomExecutablePath);

        options.Validate();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public void ServerOptions_WhenHostnameIsLoopback_Validates(string hostname)
    {
        var options = new OpenCodeServerOptions { Hostname = hostname };

        options.Validate();

        Assert.True(OpenCodeServerOptions.IsLoopbackHostname(hostname));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("example.com")]
    [InlineData("::1")]
    [InlineData("")]
    [InlineData("   ")]
    public void ServerOptions_WhenHostnameIsNotLoopback_Throws(string hostname)
    {
        var options = new OpenCodeServerOptions { Hostname = hostname };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ServerOptions_WhenPortIsNegative_Throws()
    {
        var options = new OpenCodeServerOptions { Port = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void ServerOptions_WhenPortIsInRange_Validates(int port)
    {
        var options = new OpenCodeServerOptions { Port = port };

        options.Validate();
    }

    [Fact]
    public void ServerOptions_WhenStartupTimeoutIsNotPositive_Throws()
    {
        var options = new OpenCodeServerOptions { StartupTimeout = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void ServerOptions_WhenDisposeTimeoutIsNotPositive_Throws()
    {
        var options = new OpenCodeServerOptions { DisposeTimeout = TimeSpan.FromSeconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void ServerOptions_WhenCustomExecutablePathIsWhitespace_Throws()
    {
        var options = new OpenCodeServerOptions { CustomExecutablePath = "  " };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void DiscoveryResult_NotInstalled_ReportsDegradedState()
    {
        var result = OpenCodeDiscoveryResult.NotInstalled("opencode was not found.");

        Assert.False(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.Version);
        Assert.Equal("opencode was not found.", result.ErrorMessage);
    }

    [Fact]
    public void DiscoveryResult_Detected_ReportsSupportedVersion()
    {
        var result = OpenCodeDiscoveryResult.Detected(@"C:\tools\opencode.cmd", "1.18.31");

        Assert.True(result.IsInstalled);
        Assert.True(result.IsSupportedVersion);
        Assert.Equal(@"C:\tools\opencode.cmd", result.ExecutablePath);
        Assert.Equal("1.18.31", result.Version);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void DiscoveryResult_Unsupported_RemainsInstalledButNotSupported()
    {
        var result = OpenCodeDiscoveryResult.Unsupported(@"C:\tools\opencode.cmd", "1.17.9", "below baseline");

        Assert.True(result.IsInstalled);
        Assert.False(result.IsSupportedVersion);
        Assert.Equal(@"C:\tools\opencode.cmd", result.ExecutablePath);
        Assert.Equal("1.17.9", result.Version);
        Assert.Equal("below baseline", result.ErrorMessage);
    }

    [Theory]
    [InlineData("1.18.31", "1.18.31")]
    [InlineData("opencode 1.18.31", "1.18.31")]
    [InlineData("v1.19.0", "1.19.0")]
    [InlineData("1.18.31-beta.1", "1.18.31")]
    [InlineData("version: 2.0.0\r\n", "2.0.0")]
    public void VersionParser_ParsesVersionFromOutput(string output, string expected)
    {
        Assert.True(OpenCodeVersionParser.TryParse(output, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("opencode")]
    public void VersionParser_WhenOutputHasNoVersion_ReturnsFalse(string output)
    {
        Assert.False(OpenCodeVersionParser.TryParse(output, out _));
    }

    [Theory]
    [InlineData("1.18.31", true)]
    [InlineData("1.18.30", false)]
    [InlineData("1.19.0", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.17.99", false)]
    public void VersionParser_ComparesAgainstBaseline(string version, bool expected)
    {
        Assert.Equal(expected, OpenCodeVersionParser.IsSupported(Version.Parse(version)));
        Assert.Equal(new Version(1, 18, 31), OpenCodeVersionBaseline.MinimumSupportedVersion);
    }

    [Fact]
    public void OutputScanner_ParsesListeningLine()
    {
        Assert.True(OpenCodeServerOutputScanner.TryParsePort(
            "Listening on http://127.0.0.1:54321",
            out var port));
        Assert.Equal(54321, port);
    }

    [Fact]
    public void OutputScanner_ParsesLocalhostListeningLine()
    {
        Assert.True(OpenCodeServerOutputScanner.TryParsePort(
            "server started at http://localhost:41234",
            out var port));
        Assert.Equal(41234, port);
    }

    [Fact]
    public void OutputScanner_ParsesChunkedOutputAcrossBoundaries()
    {
        var scanner = new OpenCodeServerOutputScanner();

        Assert.False(scanner.Append("Listening on http://127.0.0."));
        Assert.True(scanner.Append("1:54321\r\n"));
        Assert.Equal(54321, scanner.AssignedPort);
        Assert.Contains("54321", scanner.BufferedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no url here")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:70000")]
    [InlineData("http://192.168.1.10:54321")]
    public void OutputScanner_WhenNoValidPortIsPresent_ReturnsFalse(string text)
    {
        Assert.False(OpenCodeServerOutputScanner.TryParsePort(text, out _));
    }

    [Fact]
    public void CommandLine_ForExecutable_KeepsExecutableAndTypedArguments()
    {
        var (fileName, arguments) = OpenCodeCommandLine.Create(
            @"C:\tools\opencode.exe",
            new[] { "serve", "--port", "0" });

        Assert.Equal(@"C:\tools\opencode.exe", fileName);
        Assert.Equal(new[] { "serve", "--port", "0" }, arguments);
    }

    [Fact]
    public void CommandLine_ForBatchFile_UsesCommandInterpreterWithTypedArguments()
    {
        var (fileName, arguments) = OpenCodeCommandLine.Create(
            @"C:\tools\opencode.cmd",
            new[] { "serve", "--port", "0" });

        Assert.EndsWith("cmd.exe", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "/d", "/c", @"C:\tools\opencode.cmd", "serve", "--port", "0" },
            arguments);
    }

    [Fact]
    public void CommandLine_ForBatchFile_RejectsInterpreterMetacharacters()
    {
        Assert.Throws<ArgumentException>(() => OpenCodeCommandLine.Create(
            @"C:\tools\opencode & calc.cmd",
            ["serve"]));
    }

    [Fact]
    public void CommandLine_ForExecutable_DoesNotInterpretArgumentMetacharacters()
    {
        var (fileName, arguments) = OpenCodeCommandLine.Create(
            @"C:\tools\opencode.exe",
            ["serve", "a&b"]);

        Assert.Equal(@"C:\tools\opencode.exe", fileName);
        Assert.Equal(new[] { "serve", "a&b" }, arguments);
    }

    [Fact]
    public void CommandLine_CreateServeArguments_MatchesDocumentedFlags()
    {
        Assert.Equal(
            new[] { "serve", "--port", "0", "--hostname", "127.0.0.1" },
            OpenCodeCommandLine.CreateServeArguments("127.0.0.1", 0));
    }

    [Fact]
    public void DocParser_ExtractsOpenApiVersionAndOperations()
    {
        var response = OpenCodeDocParser.Parse(SampleDoc);

        Assert.Equal("3.1.0", response.OpenApiSpecification);
        Assert.Equal(SampleDoc, response.RawJson);
        Assert.Equal(4, response.Operations.Count);
        Assert.True(response.HasOperation("get", "/session"));
        Assert.True(response.HasOperation("POST", "/instance/dispose"));
        Assert.False(response.HasOperation("delete", "/session"));
    }

    [Fact]
    public void DocParser_WhenJsonIsMalformed_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => OpenCodeDocParser.Parse("{ not json"));
    }

    [Fact]
    public void AddOpenCodeBackend_RegistersDiscoveryServerManagerAndClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenCodeDiscoveryService>(provider.GetRequiredService<IOpenCodeDiscoveryService>());
        Assert.IsType<OpenCodeServerManager>(provider.GetRequiredService<IOpenCodeServerManager>());
        Assert.IsType<OpenCodeClient>(provider.GetRequiredService<IOpenCodeClient>());
    }

    [Fact]
    public void AddOpenCodeBackend_WhenOptionsAreInvalid_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.Configure<OpenCodeServerOptions>(options => options.Hostname = "0.0.0.0");
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OpenCodeServerOptions>>().Value);
    }

    [Fact]
    public void AddOpenCodeBackend_DoesNotReplaceExistingClientRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        var customClient = new StubOpenCodeClient();
        services.AddSingleton<IOpenCodeClient>(customClient);
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        Assert.Same(customClient, provider.GetRequiredService<IOpenCodeClient>());
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubOpenCodeClient : IOpenCodeClient
    {
        public Uri BaseUrl { get; } = new("http://127.0.0.1:1");

        public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
            string? providerId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse> CreateSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse> ForkSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> AbortSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> SendPromptAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ReplyPermissionAsync(
            string permissionId,
            OpenCodePermissionReply reply,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
