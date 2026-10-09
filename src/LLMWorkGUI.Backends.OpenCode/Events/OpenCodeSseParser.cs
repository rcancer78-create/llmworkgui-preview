using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Backends.OpenCode.Events;

public sealed class OpenCodeSseParser : IOpenCodeSseParser
{
    public const int MaxLineCharacters = 1024 * 1024;
    public const int MaxEventCharacters = 4 * 1024 * 1024;
    private readonly ILogger<OpenCodeSseParser> _logger;
    private readonly TimeProvider _timeProvider;

    public OpenCodeSseParser(
        ILogger<OpenCodeSseParser>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<OpenCodeSseParser>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<OpenCodeEventEnvelope> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        await foreach (var envelope in ParseAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    public async IAsyncEnumerable<OpenCodeEventEnvelope> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var data = new StringBuilder();
        var hasData = false;
        string? eventName = null;
        var streamFailed = false;
        var lines = new BoundedLineReader(reader);

        while (!streamFailed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line;

            try
            {
                line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _logger.LogWarning(
                    exception,
                    "OpenCode SSE stream read failed; ending event enumeration without terminating the process.");
                line = null;
                streamFailed = true;
            }

            if (streamFailed || line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    var envelope = CreateEnvelope(data.ToString(), eventName);

                    data.Clear();
                    hasData = false;
                    eventName = null;

                    if (envelope is not null)
                    {
                        yield return envelope;
                    }
                }
                else
                {
                    eventName = null;
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var field = colonIndex >= 0 ? line[..colonIndex] : line;
            var value = colonIndex >= 0 ? line[(colonIndex + 1)..] : string.Empty;

            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "data":
                    if (value.Length > MaxEventCharacters - data.Length - (hasData ? 1 : 0))
                        throw new InvalidDataException("OpenCode SSE event exceeds the supported size.");
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
                case "event":
                    eventName = value;
                    break;
                default:
                    break;
            }
        }

        if (!streamFailed && hasData)
        {
            var envelope = CreateEnvelope(data.ToString(), eventName);

            if (envelope is not null)
            {
                yield return envelope;
            }
        }
    }

    private sealed class BoundedLineReader(TextReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;
        private bool _skipLineFeed;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_position == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0) return line.Length == 0 ? null : line.ToString();
                }

                while (_position < _length)
                {
                    var character = _buffer[_position++];
                    if (_skipLineFeed)
                    {
                        _skipLineFeed = false;
                        if (character == '\n') continue;
                    }
                    if (character is '\r' or '\n')
                    {
                        _skipLineFeed = character == '\r';
                        return line.ToString();
                    }
                    if (line.Length == MaxLineCharacters)
                        throw new InvalidDataException("OpenCode SSE line exceeds the supported size.");
                    line.Append(character);
                }
            }
        }
    }

    private OpenCodeEventEnvelope? CreateEnvelope(string payload, string? eventName)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var receivedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "OpenCode SSE payload is not a JSON object; emitting a structured malformed event.");

                return OpenCodeEventEnvelope.Malformed(payload, receivedAtUtc);
            }

            var type = root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(type))
            {
                type = string.IsNullOrWhiteSpace(eventName)
                    ? OpenCodeEventEnvelope.UnknownType
                    : eventName;
            }

            var properties = root.TryGetProperty("properties", out var propertiesElement)
                && propertiesElement.ValueKind == JsonValueKind.Object
                    ? propertiesElement.Clone()
                    : OpenCodeEventEnvelope.Empty;

            return new OpenCodeEventEnvelope(type!, properties, payload, receivedAtUtc);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "OpenCode SSE payload is not valid JSON; emitting a structured malformed event.");

            return OpenCodeEventEnvelope.Malformed(payload, receivedAtUtc);
        }
    }
}
