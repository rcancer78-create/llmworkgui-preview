using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class FakeJsonRpcTransport : IJsonRpcTransport
{
    private readonly Channel<JsonRpcNotification> _notifications = Channel.CreateUnbounded<JsonRpcNotification>();
    private readonly Func<string, JsonElement?, TimeSpan?, CancellationToken, Task<JsonRpcResponse>> _handler;

    public FakeJsonRpcTransport(
        Func<string, JsonElement?, TimeSpan?, CancellationToken, Task<JsonRpcResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    /// <summary>
    /// Transport that fails every request. It is used where a bound transport instance is required
    /// as evidence but no protocol exchange takes place.
    /// </summary>
    public static FakeJsonRpcTransport Unused() =>
        new((method, _, _, _) => throw new InvalidOperationException(
            $"The unused fake transport received an unexpected '{method}' request."));

    public List<JsonRpcRequestCapture> Requests { get; } = new();

    public List<JsonRpcResponseCapture> Responses { get; } = new();

    public List<JsonRpcRequestCapture> SentNotifications { get; } = new();
    public Func<CancellationToken, Task>? NotificationHandler { get; set; }
    public Func<CancellationToken, Task>? ResponseHandler { get; set; }

    public Exception? ResponseWriteFailure { get; private set; }

    public ChannelReader<JsonRpcNotification> Notifications => _notifications.Reader;

    public int MalformedFrameCount => 0;

    public bool IsClosed { get; private set; }

    public void PublishNotification(JsonRpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        _notifications.Writer.TryWrite(notification);
    }

    public void CompleteNotifications() => _notifications.Writer.TryComplete();

    public void FailResponseWrites(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ResponseWriteFailure = exception;
    }

    public Task<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new JsonRpcRequestCapture(method, parameters, timeout));

        return _handler(method, parameters, timeout, cancellationToken);
    }

    public Task SendNotificationAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SentNotifications.Add(new JsonRpcRequestCapture(method, parameters, null));
        return NotificationHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task SendResponseAsync(
        JsonElement id,
        JsonElement? result = null,
        JsonRpcError? error = null,
        CancellationToken cancellationToken = default)
    {
        Responses.Add(new JsonRpcResponseCapture(id, result, error));

        return ResponseWriteFailure is { } failure
            ? Task.FromException(failure)
            : ResponseHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IsClosed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static FakeJsonRpcTransport RespondingWith(JsonElement result) =>
        new((_, _, _, _) => Task.FromResult(new JsonRpcResponse
        {
            Id = JsonSerializer.SerializeToElement(1),
            Result = result
        }));

    public static FakeJsonRpcTransport RespondingWithError(int code, string message) =>
        new((_, _, _, _) => Task.FromResult(new JsonRpcResponse
        {
            Id = JsonSerializer.SerializeToElement(1),
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        }));

    public static FakeJsonRpcTransport RespondingWithEmptyPayload() =>
        new((_, _, _, _) => Task.FromResult(new JsonRpcResponse
        {
            Id = JsonSerializer.SerializeToElement(1)
        }));

    public static FakeJsonRpcTransport FailingWith(Exception exception) =>
        new((_, _, _, _) => Task.FromException<JsonRpcResponse>(exception));

    public static JsonRpcResponse CreateResultResponse(JsonElement result) =>
        new()
        {
            Id = JsonSerializer.SerializeToElement(1),
            Result = result
        };

    public static FakeJsonRpcTransport RespondingByMethod(Func<string, JsonRpcResponse> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        return new FakeJsonRpcTransport((method, _, _, _) => Task.FromResult(responder(method)));
    }

    internal sealed record JsonRpcRequestCapture(
        string Method,
        JsonElement? Parameters,
        TimeSpan? Timeout);

    internal sealed record JsonRpcResponseCapture(
        JsonElement Id,
        JsonElement? Result,
        JsonRpcError? Error);
}
