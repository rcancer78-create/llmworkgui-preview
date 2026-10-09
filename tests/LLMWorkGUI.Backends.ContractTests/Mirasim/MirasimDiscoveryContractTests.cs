using System.Net;
using System.Net.Sockets;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class MirasimDiscoveryContractTests
{
    [Fact]
    public async Task HealthProbe_WithRunningLoopbackHost_ParsesVersionAndStatus()
    {
        var healthFixture = File.ReadAllText(FixturePath("health-response-sample.json"));

        await using var server = await LoopbackMirasimServer.StartAsync();
        server.HealthResponseJson = healthFixture;

        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient, server.Port);

        var status = await client.ProbeHealthAsync();

        Assert.True(status.Ok);
        Assert.Equal("mirasim", status.Name);
        Assert.Equal("0.0.354", status.Version);
        Assert.Equal(23824, status.Pid);
        Assert.Equal("11e9703cacf9d5c1", status.InstanceId);
        Assert.Equal(5448, status.Uptime);
        Assert.True(status.IsSupportedVersion);
        Assert.Null(status.ErrorMessage);

        Assert.Equal(new[] { "/api/health" }, server.RequestedPaths);
        Assert.Null(server.LastHealthAuthorization);
    }

    [Fact]
    public async Task HealthProbe_WithUnsupportedVersion_ReportsNotSupported()
    {
        await using var server = await LoopbackMirasimServer.StartAsync();
        server.HealthResponseJson = """
        {
          "ok": true,
          "name": "mirasim",
          "version": "0.1.0",
          "uptime": 12,
          "pid": 4242,
          "instance": "0000000000000000"
        }
        """;

        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient, server.Port);

        var status = await client.ProbeHealthAsync();

        Assert.True(status.Ok);
        Assert.Equal("0.1.0", status.Version);
        Assert.False(status.IsSupportedVersion);
        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public async Task HealthProbe_WhenHostIsDown_ReturnsUnavailableStatusDeterministically()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = CreateClient(httpClient, port: 4970, requestTimeout: TimeSpan.FromSeconds(2));

        var status = await client.ProbeHealthAsync();

        Assert.False(status.Ok);
        Assert.False(status.IsSupportedVersion);
        Assert.False(string.IsNullOrWhiteSpace(status.ErrorMessage));
        Assert.Contains("unreachable", status.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("/api/health", Assert.Single(handler.Requests));
    }

    [Fact]
    public void Options_RejectsNonLoopbackHostname()
    {
        Assert.True(MirasimOptions.IsLoopbackHostname("127.0.0.1"));
        Assert.True(MirasimOptions.IsLoopbackHostname("localhost"));
        Assert.False(MirasimOptions.IsLoopbackHostname("192.168.1.20"));
        Assert.False(MirasimOptions.IsLoopbackHostname("example.com"));
        Assert.False(MirasimOptions.IsLoopbackHostname(null));

        Assert.Throws<ArgumentException>(
            () => new MirasimOptions { Hostname = "example.com" }.Validate());

        using var httpClient = CreateHttpClient();

        Assert.Throws<ArgumentException>(
            () => new MirasimClient(
                httpClient,
                Options.Create(new MirasimOptions { Hostname = "example.com" })));
    }

    [Fact]
    public async Task AuthenticatedProbe_WithoutToken_ReturnsUnauthorizedChallenge()
    {
        const string Token = "contract-test-token";
        var unauthorizedFixture = File.ReadAllText(FixturePath("unauthorized-response-sample.json"));

        await using var server = await LoopbackMirasimServer.StartAsync();
        server.UnauthorizedResponseJson = unauthorizedFixture;
        server.AcceptedToken = Token;

        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient, server.Port);

        var withoutToken = await client.ProbeAuthenticatedEndpointAsync();

        Assert.False(withoutToken);
        Assert.Null(server.LastStateAuthorization);

        var withWrongToken = await client.ProbeAuthenticatedEndpointAsync("wrong-contract-token");

        Assert.False(withWrongToken);

        var withToken = await client.ProbeAuthenticatedEndpointAsync(Token);

        Assert.True(withToken);
        Assert.Equal($"Bearer {Token}", server.LastStateAuthorization);
        Assert.Equal(
            new[] { "/api/state", "/api/state", "/api/state" },
            server.RequestedPaths);
    }

    [Fact]
    public async Task Security_TokenIsNotLoggedOrLeakedInExceptions()
    {
        const string Token = "contract-secret-token-42";

        await using var server = await LoopbackMirasimServer.StartAsync();
        server.AuthenticatedFailureStatusCode = HttpStatusCode.InternalServerError;

        var logger = new CapturingLogger<MirasimClient>();
        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient, server.Port, logger: logger);

        var exception = await Assert.ThrowsAsync<MirasimClientException>(
            () => client.ProbeAuthenticatedEndpointAsync(Token));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.False(exception.Message.Contains(Token, StringComparison.Ordinal));
        Assert.False(exception.ToString().Contains(Token, StringComparison.Ordinal));
        Assert.False(client.BaseUrl.ToString().Contains(Token, StringComparison.Ordinal));
        Assert.False(string.Join('\n', logger.Messages).Contains(Token, StringComparison.Ordinal));

        Assert.Equal($"Bearer {Token}", server.LastStateAuthorization);

        var redacted = new MirasimClientException(
            $"The probe failed with header Authorization: Bearer {Token}.",
            HttpStatusCode.BadRequest);

        Assert.False(redacted.Message.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void AddMirasimBackend_RegistersSingletonLoopbackClientWithDefaults()
    {
        var services = new ServiceCollection();
        services.AddMirasimBackend();

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IMirasimClient>();
        var options = provider.GetRequiredService<IOptions<MirasimOptions>>().Value;

        Assert.IsType<MirasimClient>(client);
        Assert.Same(client, provider.GetRequiredService<IMirasimClient>());
        Assert.Equal("127.0.0.1", options.Hostname);
        Assert.Equal(4970, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout);
        Assert.Equal(new Uri("http://127.0.0.1:4970"), client.BaseUrl);
    }

    [Fact]
    public void AddMirasimBackend_WhenHostnameIsNotLoopback_FailsOptionsValidation()
    {
        var services = new ServiceCollection();
        services.Configure<MirasimOptions>(options => options.Hostname = "example.com");
        services.AddMirasimBackend();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<MirasimOptions>>().Value);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static MirasimClient CreateClient(
        HttpClient httpClient,
        int port,
        TimeSpan? requestTimeout = null,
        ILogger<MirasimClient>? logger = null)
    {
        return new MirasimClient(
            httpClient,
            Options.Create(new MirasimOptions
            {
                Hostname = "127.0.0.1",
                Port = port,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10)
            }),
            logger);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "mirasim", fileName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            _handler = handler;
        }

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsolutePath ?? string.Empty);

            return _handler(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class LoopbackMirasimServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;

        private LoopbackMirasimServer(int port)
        {
            Port = port;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public int Port { get; }

        public string HealthResponseJson { get; set; } = """{"ok":true}""";

        public string UnauthorizedResponseJson { get; set; } = """{"error":"local access token required"}""";

        public string AuthenticatedFailureResponseJson { get; set; } = """{"error":"internal error"}""";

        public string? AcceptedToken { get; set; }

        public HttpStatusCode AuthenticatedFailureStatusCode { get; set; } = HttpStatusCode.InternalServerError;

        public string? LastHealthAuthorization { get; private set; }

        public string? LastStateAuthorization { get; private set; }

        public List<string> RequestedPaths { get; } = new();

        public static Task<LoopbackMirasimServer> StartAsync()
        {
            var server = new LoopbackMirasimServer(GetAvailablePort());

            server._listener.Start();
            server._listenTask = Task.Run(server.ListenLoopAsync);

            return Task.FromResult(server);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);

                    _ = Task.Run(() => HandleRequestAsync(context, _cts.Token));
                }
                catch (Exception exception)
                    when (exception is HttpListenerException or ObjectDisposedException || _cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var request = context.Request;
                var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

                RequestedPaths.Add(path);

                if (string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
                {
                    LastHealthAuthorization = request.Headers["Authorization"];

                    await WriteBodyAsync(
                        context.Response,
                        HttpStatusCode.OK,
                        HealthResponseJson,
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                if (string.Equals(path, "/api/state", StringComparison.OrdinalIgnoreCase))
                {
                    var authorization = request.Headers["Authorization"];

                    LastStateAuthorization = authorization;

                    if (AcceptedToken is not null &&
                        string.Equals(authorization, $"Bearer {AcceptedToken}", StringComparison.Ordinal))
                    {
                        await WriteBodyAsync(
                            context.Response,
                            HttpStatusCode.OK,
                            """{"ok":true}""",
                            cancellationToken).ConfigureAwait(false);

                        return;
                    }

                    if (AcceptedToken is null)
                    {
                        await WriteBodyAsync(
                            context.Response,
                            AuthenticatedFailureStatusCode,
                            AuthenticatedFailureResponseJson,
                            cancellationToken).ConfigureAwait(false);

                        return;
                    }

                    await WriteBodyAsync(
                        context.Response,
                        HttpStatusCode.Unauthorized,
                        UnauthorizedResponseJson,
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                await WriteBodyAsync(
                    context.Response,
                    HttpStatusCode.NotFound,
                    """{"error":"not found"}""",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // The client disconnected or the server is shutting down.
                }
            }
        }

        private static async Task WriteBodyAsync(
            HttpListenerResponse response,
            HttpStatusCode statusCode,
            string json,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json);

            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;

            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            if (_listenTask is not null)
            {
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch
                {
                    // Listener shutdown.
                }
            }

            _listener.Close();
            _cts.Dispose();
        }
    }
}
