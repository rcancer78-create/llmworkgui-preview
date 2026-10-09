using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Backends.OpenCode.Events;

public sealed class OpenCodeEventSpool : IAsyncDisposable
{
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;
    private readonly ILogger _logger;
    private readonly object _stateGate = new();
    private bool _isOverflowed;

    public OpenCodeEventSpool(
        string directory,
        int queueCapacity,
        string? fileName = null,
        ILogger? logger = null)
        : this(directory, queueCapacity, fileName, logger, streamFactory: null)
    {
    }

    internal OpenCodeEventSpool(
        string directory,
        int queueCapacity,
        string? fileName,
        ILogger? logger,
        Func<string, Stream>? streamFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Queue capacity must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(fileName) &&
            (Path.IsPathRooted(fileName) || fileName is "." or ".." ||
             fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
             fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("The spool name must be a single file name inside its directory.", nameof(fileName));
        }

        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(
            directory,
            string.IsNullOrWhiteSpace(fileName)
                ? Abstractions.OpenCode.Events.OpenCodeStreamOptions.DefaultSpoolFileName
                : fileName);

        _logger = logger ?? NullLogger.Instance;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writerTask = WriteLoopAsync(streamFactory ?? (path => new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true)));
    }

    public string FilePath { get; }

    public bool IsOverflowed
    {
        get
        {
            lock (_stateGate)
            {
                return _isOverflowed;
            }
        }
    }

    public static string ToJsonLine(string rawJson)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        return rawJson.Contains('\n') || rawJson.Contains('\r')
            ? rawJson.ReplaceLineEndings(" ")
            : rawJson;
    }

    public async ValueTask<bool> EnqueueAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        line = ToJsonLine(new CredentialTextRedactor().RedactDiagnostic(line));

        if (_writerTask.IsCompleted)
        {
            MarkOverflowed();
            _logger.LogWarning(
                "OpenCode event spool writer is not running; the event line could not be persisted to disk.");
            return true;
        }

        if (_channel.Writer.TryWrite(line))
        {
            return false;
        }

        MarkOverflowed();

        if (_writerTask.IsCompleted)
        {
            _logger.LogWarning(
                "OpenCode event spool writer is not running; the event line could not be persisted to disk.");

            return true;
        }

        try
        {
            await _channel.Writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning(
                "OpenCode event spool is closed; the event line could not be persisted to disk.");
        }

        return true;
    }

    public async ValueTask CompleteAsync()
    {
        _channel.Writer.TryComplete();

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkOverflowed();
            _logger.LogWarning(exception, "OpenCode event spool writer terminated with an error.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
    }

    private async Task WriteLoopAsync(Func<string, Stream> streamFactory)
    {
        try
        {
            await using var stream = streamFactory(FilePath);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            MarkOverflowed();
            _channel.Writer.TryComplete(exception);
            throw;
        }
    }

    private void MarkOverflowed()
    {
        lock (_stateGate)
        {
            _isOverflowed = true;
        }
    }
}
