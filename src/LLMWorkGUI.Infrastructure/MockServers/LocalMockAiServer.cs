using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.MockServers;

/// <summary>
/// In-process HTTP mock server simulating OpenAI-compatible endpoints (/v1/models, /v1/chat/completions)
/// on a local loopback port for testing and UI simulation.
/// </summary>
public sealed class LocalMockAiServer : IAsyncDisposable, IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private bool _disposed;

    public int Port { get; }
    public string BaseUrl { get; }

    public string? ExpectedApiKey { get; set; }
    public int? SimulatedStatusCode { get; set; }
    public TimeSpan? SimulatedDelay { get; set; }

    /// <summary>
    /// When set, the generated chat-completions payload echoes this model instead of the first
    /// configured one, so a test can simulate a provider that answers with the wrong model.
    /// </summary>
    public string? ChatCompletionModelOverride { get; set; }

    /// <summary>
    /// When set, the chat-completions endpoint writes this payload verbatim instead of generating one,
    /// so a test can exercise malformed or structurally invalid provider responses.
    /// </summary>
    public string? ChatCompletionRawResponseOverride { get; set; }

    public List<DiscoveredModelInfo> Models { get; } = new()
    {
        new DiscoveredModelInfo("mock-gpt-4o", "Mock GPT-4o", "mock"),
        new DiscoveredModelInfo("mock-claude-3-5-sonnet", "Mock Claude 3.5 Sonnet", "mock")
    };

    public Dictionary<string, string> LastReceivedHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int RequestCount { get; private set; }

    public LocalMockAiServer(int? explicitPort = null)
    {
        Port = explicitPort ?? GetAvailablePort();
        BaseUrl = $"http://127.0.0.1:{Port}/v1";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public async Task StartAsync()
    {
        if (_listener.IsListening)
        {
            return;
        }

        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
        await Task.Yield();
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
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException || _cts.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            RequestCount++;
            var req = context.Request;
            var resp = context.Response;

            lock (LastReceivedHeaders)
            {
                LastReceivedHeaders.Clear();
                foreach (string? key in req.Headers.AllKeys)
                {
                    if (key != null)
                    {
                        LastReceivedHeaders[key] = req.Headers[key] ?? string.Empty;
                    }
                }
            }

            if (SimulatedDelay.HasValue && SimulatedDelay.Value > TimeSpan.Zero)
            {
                await Task.Delay(SimulatedDelay.Value, ct).ConfigureAwait(false);
            }

            if (SimulatedStatusCode.HasValue)
            {
                resp.StatusCode = SimulatedStatusCode.Value;
                resp.ContentType = "application/json; charset=utf-8";
                var errorPayload = JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        message = $"Simulated error with status {SimulatedStatusCode.Value}",
                        type = "mock_server_error",
                        code = SimulatedStatusCode.Value
                    }
                });
                await WriteResponseAsync(resp, errorPayload, ct).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(ExpectedApiKey))
            {
                var authHeader = req.Headers["Authorization"];
                var expectedHeader = $"Bearer {ExpectedApiKey}";
                if (!string.Equals(authHeader, expectedHeader, StringComparison.Ordinal))
                {
                    resp.StatusCode = 401;
                    resp.ContentType = "application/json; charset=utf-8";
                    var authErrorPayload = JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            message = "Incorrect API key provided.",
                            type = "invalid_request_error"
                        }
                    });
                    await WriteResponseAsync(resp, authErrorPayload, ct).ConfigureAwait(false);
                    return;
                }
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

            if (string.Equals(path, "/v1/models", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/models", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";

                var modelItems = Models.Select(m => new
                {
                    id = m.Id,
                    @object = "model",
                    created = 1700000000,
                    owned_by = m.OwnedBy ?? "mock"
                }).ToArray();

                var listPayload = JsonSerializer.Serialize(new
                {
                    @object = "list",
                    data = modelItems
                });

                await WriteResponseAsync(resp, listPayload, ct).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";

                var completionPayload = ChatCompletionRawResponseOverride ?? JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-mock-12345",
                    @object = "chat.completion",
                    created = 1700000000,
                    model = ChatCompletionModelOverride ?? Models.FirstOrDefault()?.Id ?? "mock-gpt-4o",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = "Hello! I am a simulated response from LocalMockAiServer."
                            },
                            finish_reason = "stop"
                        }
                    }
                });

                await WriteResponseAsync(resp, completionPayload, ct).ConfigureAwait(false);
                return;
            }

            resp.StatusCode = 404;
            resp.ContentType = "application/json; charset=utf-8";
            var notFound = JsonSerializer.Serialize(new
            {
                error = new { message = $"Not found: {path}", type = "invalid_request_error" }
            });
            await WriteResponseAsync(resp, notFound, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // Ignore secondary failures on closing
            }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string jsonContent, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonContent);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
        response.Close();
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch
            {
                // Listener cancellation expected
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Ignore on shutdown
        }
        _cts.Dispose();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
