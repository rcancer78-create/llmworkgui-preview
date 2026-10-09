using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Duplex newline-delimited JSON-RPC 2.0 transport over the stdio streams of a managed
/// <c>cursor-agent acp</c> process (ADR-0003 §1). Outbound frames are UTF-8 with a single newline
/// terminator; inbound frames are correlated to requests by id. Malformed lines are counted and
/// skipped without breaking the transport, outbound frames flow through a bounded queue
/// (backpressure), and closing fails outstanding requests deterministically.
/// </summary>
public sealed class JsonRpcStdioTransport : IJsonRpcWriteGateTransport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _agentStandardOutput;
    private readonly Stream _agentStandardInput;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CursorAcpOptions _options;
    private readonly int _maximumInboundFrameCharacters;
    private readonly Channel<OutboundFrame> _outbound;
    private readonly Channel<JsonRpcNotification> _notifications;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TaskCompletionSource, byte> _pendingResponses = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private readonly Task _writeLoop;

    private int _nextRequestId;
    private int _malformedFrameCount;
    private int _droppedNotificationCount;
    private int _unmatchedResponseCount;
    private int _closed;
    private readonly object _cleanupGate = new();
    private Task? _closeTask;
    private Task? _disposeTask;

    public JsonRpcStdioTransport(
        Stream agentStandardOutput,
        Stream agentStandardInput,
        CursorAcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(agentStandardOutput);
        ArgumentNullException.ThrowIfNull(agentStandardInput);

        if (!agentStandardOutput.CanRead)
        {
            throw new ArgumentException(
                "The agent standard output stream must be readable.",
                nameof(agentStandardOutput));
        }

        if (!agentStandardInput.CanWrite)
        {
            throw new ArgumentException(
                "The agent standard input stream must be writable.",
                nameof(agentStandardInput));
        }

        _agentStandardOutput = agentStandardOutput;
        _agentStandardInput = agentStandardInput;
        _options = options ?? new CursorAcpOptions();
        _options.Validate();
        _maximumInboundFrameCharacters = _options.MaximumInboundFrameCharacters;

        _outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(_options.OutboundQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _notifications = Channel.CreateBounded<JsonRpcNotification>(
            new BoundedChannelOptions(_options.NotificationQueueCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        _reader = new StreamReader(
            agentStandardOutput,
            Utf8NoBom,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        _writer = new StreamWriter(agentStandardInput, Utf8NoBom, bufferSize: 4096, leaveOpen: true);

        _readLoop = Task.Run(ReadLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    public ChannelReader<JsonRpcNotification> Notifications => _notifications.Reader;

    public int MalformedFrameCount => Volatile.Read(ref _malformedFrameCount);

    /// <summary>Inbound notifications rejected on overflow; the transport fails closed at the first loss.</summary>
    public int DroppedNotificationCount => Volatile.Read(ref _droppedNotificationCount);

    /// <summary>Responses whose id did not match any outstanding request.</summary>
    public int UnmatchedResponseCount => Volatile.Read(ref _unmatchedResponseCount);

    public int PendingRequestCount => _pending.Count;

    public Task<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => SendRequestCoreAsync(method, parameters, timeout, null, null, default, cancellationToken);

    public Task<JsonRpcResponse> SendRequestWithGateAsync(string method, JsonElement parameters,
        Func<JsonElement, CancellationToken, Task<bool>> authorizeWrite, Action dispatchStarted,
        TimeSpan? timeout = null, CancellationToken beforeWriteCancellationToken = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizeWrite);
        ArgumentNullException.ThrowIfNull(dispatchStarted);
        return SendRequestCoreAsync(method, parameters, timeout, authorizeWrite, dispatchStarted,
            beforeWriteCancellationToken, cancellationToken);
    }

    private async Task<JsonRpcResponse> SendRequestCoreAsync(string method, JsonElement? parameters, TimeSpan? timeout,
        Func<JsonElement, CancellationToken, Task<bool>>? authorizeWrite, Action? dispatchStarted,
        CancellationToken beforeWriteCancellationToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextRequestId);
        var key = "n:" + id.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(key, completion))
        {
            throw new JsonRpcTransportException(
                JsonRpcTransportFailureKind.WriteFailed,
                $"A JSON-RPC request with id {id} is already pending.");
        }

        using var writeCancellation = authorizeWrite is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, beforeWriteCancellationToken, _lifetime.Token);
        var frame = new OutboundFrame(BuildRequestFrame(id, method, parameters), key, authorizeWrite,
            dispatchStarted, writeCancellation?.Token ?? _lifetime.Token);
        try
        {
            await EnqueueFrameAsync(frame, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(key, out _);
            await CancelAndRetireGateAsync(frame, writeCancellation).ConfigureAwait(false);
            throw;
        }

        var effectiveTimeout = timeout ?? _options.RequestTimeout;

        try
        {
            return await completion.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(key, out _);

            throw new JsonRpcTransportException(
                JsonRpcTransportFailureKind.RequestTimedOut,
                $"The JSON-RPC request '{method}' (id {id}) did not receive a response within {effectiveTimeout}.");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(key, out _);
            throw;
        }
        finally
        {
            // Retire queued work, or wait until an already-started authorization reports its grant.
            // The caller must not publish "not dispatched" before that outcome is known.
            await CancelAndRetireGateAsync(frame, writeCancellation).ConfigureAwait(false);
        }
    }

    public async Task SendNotificationAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        await EnqueueFrameAsync(BuildNotificationFrame(method, parameters), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SendResponseAsync(
        JsonElement id,
        JsonElement? result = null,
        JsonRpcError? error = null,
        CancellationToken cancellationToken = default)
    {
        if (result is null && error is null)
        {
            throw new ArgumentException(
                "A JSON-RPC response requires exactly one of a result or an error object.",
                nameof(result));
        }

        if (result is not null && error is not null)
        {
            throw new ArgumentException(
                "A JSON-RPC response must not carry both a result and an error object.",
                nameof(error));
        }

        if (Volatile.Read(ref _closed) != 0)
            throw new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "The JSON-RPC transport is closed.");
        // Establish token ownership before registration, so a concurrently disposed lifetime
        // cannot leave an unreachable response waiter in the transport's dictionary.
        var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Caller cancellation may stop waiting while a noncooperative stream still owns the frame.
        // Observe its eventual failure; never retry or report the queued frame as sent.
        _ = written.Task.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _pendingResponses.TryAdd(written, 0);
        var enqueued = false;
        try
        {
            await EnqueueFrameAsync(new OutboundFrame(BuildResponseFrame(id, result, error), null, null, null,
                writeCancellation.Token, written, writeCancellation), cancellationToken).ConfigureAwait(false);
            enqueued = true; // The writer now owns the cancellation source until physical retirement.
            await written.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingResponses.TryRemove(written, out _);
            if (!enqueued) writeCancellation.Dispose();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AwaitCleanupAsync(GetCloseTask(), cancellationToken).ConfigureAwait(false);
    }

    private Task GetCloseTask()
    {
        lock (_cleanupGate)
        {
            Interlocked.Exchange(ref _closed, 1);
            return _closeTask ??= Task.Run(CloseCoreAsync);
        }
    }

    private async Task CloseCoreAsync()
    {
        _outbound.Writer.TryComplete();
        CompletePendingRequests(new JsonRpcTransportException(
            JsonRpcTransportFailureKind.TransportClosed, "The JSON-RPC transport was closed."));

        try
        {
            await _writeLoop.WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _lifetime.Cancel();

        // The shared task retains the streams until both loops actually finish. A caller's
        // bounded wait can expire, but that expiry is never reported as successful cleanup.
        await Task.WhenAll(_readLoop, _writeLoop).ConfigureAwait(false);
        _notifications.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Task disposing;
        lock (_cleanupGate) { disposing = _disposeTask ??= Task.Run(DisposeCoreAsync); }
        await AwaitCleanupAsync(disposing, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AwaitCleanupAsync(Task cleanup, CancellationToken cancellationToken)
    {
        try { await cleanup.WaitAsync(CleanupWaitTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException exception)
        {
            throw new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed,
                "JSON-RPC cleanup remains unconfirmed; the existing cleanup task and streams are retained.", exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await GetCloseTask().ConfigureAwait(false);
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _agentStandardOutput.DisposeAsync().ConfigureAwait(false);
        await _agentStandardInput.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private Task EnqueueFrameAsync(string frame, CancellationToken cancellationToken)
        => EnqueueFrameAsync(new OutboundFrame(frame, null, null, null, _lifetime.Token), cancellationToken);

    private async Task EnqueueFrameAsync(OutboundFrame frame, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new JsonRpcTransportException(
                JsonRpcTransportFailureKind.TransportClosed,
                "The JSON-RPC transport is closed.");
        }

        try
        {
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new JsonRpcTransportException(
                JsonRpcTransportFailureKind.TransportClosed,
                "The JSON-RPC transport is closed.",
                exception);
        }
    }

    private async Task ReadLoopAsync()
    {
        JsonRpcTransportException? failure = null;
        try
        {
            await foreach (var line in ReadInboundFramesAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleInboundLine(line.Trim());
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or JsonRpcTransportException)
        {
            failure = exception as JsonRpcTransportException ?? new JsonRpcTransportException(
                JsonRpcTransportFailureKind.ReadFailed, "The JSON-RPC inbound stream failed.", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            if (failure is not null) _lifetime.Cancel();
            CompletePendingRequests(failure ?? new JsonRpcTransportException(
                JsonRpcTransportFailureKind.TransportClosed,
                "The agent standard output reached EOF or the transport was closed."));

            _notifications.Writer.TryComplete(failure);
            _outbound.Writer.TryComplete();
        }
    }

    private async IAsyncEnumerable<string> ReadInboundFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[Math.Min(4096, _maximumInboundFrameCharacters)];
        var line = new StringBuilder(Math.Min(4096, _maximumInboundFrameCharacters));
        var skipLineFeed = false;
        while (true)
        {
            var read = await _reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length > 0) yield return line.ToString();
                yield break;
            }
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (skipLineFeed && value == '\n') { skipLineFeed = false; continue; }
                skipLineFeed = false;
                if (value is '\r' or '\n')
                {
                    var frame = line.ToString();
                    line.Clear();
                    skipLineFeed = value == '\r';
                    yield return frame;
                }
                else
                {
                    if (line.Length == _maximumInboundFrameCharacters)
                        throw new JsonRpcTransportException(JsonRpcTransportFailureKind.ReadFailed,
                            "The inbound JSON-RPC frame exceeded the configured size limit.");
                    line.Append(value);
                }
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        JsonRpcTransportException? writeFailure = null;
        try
        {
            await foreach (var frame in _outbound.Reader
                               .ReadAllAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                if (frame.AuthorizeWrite is not null)
                {
                    // This is after queue/backpressure waiting, immediately before this exact frame.
                    if (frame.RequestKey is null || !_pending.ContainsKey(frame.RequestKey))
                    {
                        await frame.RetireGateAsync().ConfigureAwait(false);
                        continue;
                    }
                    if (!frame.TryBeginAuthorization()) continue;
                    try
                    {
                        frame.WriteCancellationToken.ThrowIfCancellationRequested();
                        using var document = JsonDocument.Parse(frame.Payload);
                        if (!await frame.AuthorizeWrite(document.RootElement.GetProperty("params"), frame.WriteCancellationToken).ConfigureAwait(false))
                            throw new JsonRpcTransportException(JsonRpcTransportFailureKind.WriteFailed,
                                "Project authorization refused this prompt before the stdio write.");
                        // Once the durable one-use grant is consumed, failure/cancellation is uncertain.
                        frame.DispatchStarted?.Invoke();
                    }
                    catch (Exception exception)
                    {
                        if (_pending.TryRemove(frame.RequestKey, out var denied)) denied.TrySetException(exception);
                        continue;
                    }
                    finally { frame.CompleteAuthorization(); }
                }
                try
                {
                    await _writer.WriteLineAsync(frame.Payload.AsMemory(), frame.WriteCancellationToken).ConfigureAwait(false);
                    await _writer.FlushAsync(frame.WriteCancellationToken).ConfigureAwait(false);
                    frame.WriteCompletion?.TrySetResult();
                }
                catch (OperationCanceledException exception)
                {
                    var failure = new JsonRpcTransportException(JsonRpcTransportFailureKind.WriteFailed,
                        "The JSON-RPC frame write was cancelled before completion could be confirmed.", exception);
                    frame.WriteCompletion?.TrySetException(failure);
                    if (frame.AuthorizeWrite is not null && !_lifetime.IsCancellationRequested)
                    {
                        if (frame.RequestKey is not null && _pending.TryRemove(frame.RequestKey, out var cancelled))
                            cancelled.TrySetCanceled(frame.WriteCancellationToken);
                        continue;
                    }
                    writeFailure = failure;
                    break;
                }
                catch (Exception exception)
                {
                    writeFailure = new JsonRpcTransportException(
                        JsonRpcTransportFailureKind.WriteFailed,
                        "Failed to write a JSON-RPC frame to the agent standard input.",
                        exception);
                    frame.WriteCompletion?.TrySetException(writeFailure);
                    CompletePendingRequests(writeFailure);

                    break;
                }
                finally { frame.OwnedWriteCancellation?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            var failure = writeFailure ?? new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed,
                "The JSON-RPC writer closed before the queued frame was written.");
            if (writeFailure is not null)
            {
                Interlocked.Exchange(ref _closed, 1);
                _lifetime.Cancel();
                CompletePendingRequests(writeFailure);
            }
            _outbound.Writer.TryComplete();
            while (_outbound.Reader.TryRead(out var queued))
            {
                queued.WriteCompletion?.TrySetException(failure);
                queued.OwnedWriteCancellation?.Dispose();
                await queued.RetireGateAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task CancelAndRetireGateAsync(OutboundFrame frame, CancellationTokenSource? writeCancellation)
    {
        try { writeCancellation?.Cancel(); }
        finally { await frame.RetireGateAsync().ConfigureAwait(false); }
    }

    private sealed record OutboundFrame(string Payload, string? RequestKey,
        Func<JsonElement, CancellationToken, Task<bool>>? AuthorizeWrite, Action? DispatchStarted,
        CancellationToken WriteCancellationToken, TaskCompletionSource? WriteCompletion = null,
        CancellationTokenSource? OwnedWriteCancellation = null)
    {
        // Exactly one actor starts authorization or retires a queued frame. A running callback
        // owns its completion, including cancellation and transport shutdown.
        private int _authorizationState;
        private readonly TaskCompletionSource _authorizationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryBeginAuthorization() => Interlocked.CompareExchange(ref _authorizationState, 1, 0) == 0;
        public void CompleteAuthorization()
        {
            Volatile.Write(ref _authorizationState, 2);
            _authorizationFinished.TrySetResult();
        }
        public Task RetireGateAsync()
        {
            if (AuthorizeWrite is null) return Task.CompletedTask;
            if (Interlocked.CompareExchange(ref _authorizationState, 3, 0) == 0)
                _authorizationFinished.TrySetResult();
            return _authorizationFinished.Task;
        }
    }

    private void HandleInboundLine(string line)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref _malformedFrameCount);
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Interlocked.Increment(ref _malformedFrameCount);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.ValueKind == JsonValueKind.String)
            {
                var notification = new JsonRpcNotification
                {
                    Method = methodElement.GetString()!,
                    Parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : null,
                    Id = root.TryGetProperty("id", out var notificationId)
                        ? notificationId.Clone()
                        : null
                };

                if (!_notifications.Writer.TryWrite(notification))
                {
                    Interlocked.Increment(ref _droppedNotificationCount);
                    throw new JsonRpcTransportException(JsonRpcTransportFailureKind.ReadFailed,
                        "The JSON-RPC notification queue overflowed; event delivery cannot be confirmed.");
                }

                return;
            }

            if (root.TryGetProperty("id", out var responseId))
            {
                HandleResponse(root, responseId);
                return;
            }

            Interlocked.Increment(ref _malformedFrameCount);
        }
    }

    private void HandleResponse(JsonElement root, JsonElement responseId)
    {
        var key = ToCorrelationKey(responseId);

        if (key is null)
        {
            Interlocked.Increment(ref _malformedFrameCount);
            return;
        }

        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : (JsonElement?)null;

        var error = root.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.Object
            ? ParseError(errorElement)
            : null;

        if (result is null && error is null)
        {
            Interlocked.Increment(ref _malformedFrameCount);
            return;
        }

        if (_pending.TryRemove(key, out var completion))
        {
            completion.TrySetResult(new JsonRpcResponse
            {
                Id = responseId.Clone(),
                Result = result,
                Error = error
            });

            return;
        }

        Interlocked.Increment(ref _unmatchedResponseCount);
    }

    private static JsonRpcError ParseError(JsonElement errorElement)
    {
        var code = errorElement.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.Number &&
            codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode
            : 0;

        var message = errorElement.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? string.Empty
            : string.Empty;

        var data = errorElement.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : (JsonElement?)null;

        return new JsonRpcError
        {
            Code = code,
            Message = message,
            Data = data
        };
    }

    private static string? ToCorrelationKey(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var numeric) =>
                "n:" + numeric.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => "n:" + id.GetRawText(),
            JsonValueKind.String => "s:" + id.GetString(),
            _ => null
        };
    }

    private static string BuildRequestFrame(int id, string method, JsonElement? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);

            if (parameters is { } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildNotificationFrame(string method, JsonElement? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);

            if (parameters is { } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildResponseFrame(JsonElement id, JsonElement? result, JsonRpcError? error)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);

            if (result is { } resultValue)
            {
                writer.WritePropertyName("result");
                resultValue.WriteTo(writer);
            }
            else if (error is { } errorValue)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", errorValue.Code);
                writer.WriteString("message", errorValue.Message);

                if (errorValue.Data is { } data)
                {
                    writer.WritePropertyName("data");
                    data.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void CompletePendingRequests(JsonRpcTransportException failure)
    {
        foreach (var response in _pendingResponses.Keys) response.TrySetException(failure);
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetException(failure);
            }
        }
    }
}
