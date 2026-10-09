using System.Text;

namespace LLMWorkGUI.Infrastructure.Http;

/// <summary>Reads logical CR, LF or CRLF lines without allocating an unbounded line.
/// The caller retains ownership of the supplied reader.</summary>
internal sealed class BoundedTextLineReader
{
    private readonly StreamReader _reader;
    private readonly int _maximumChars;
    // StreamReader buffers bytes itself. A larger character request can try to fill
    // past a complete Unicode line when a full byte buffer decodes to fewer chars,
    // blocking an interactive stream on its withheld next frame.
    private readonly char[] _buffer = new char[1];
    private int _offset;
    private int _count;
    private bool _skipLf;

    public BoundedTextLineReader(StreamReader reader, int maximumChars)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChars);
        _reader = reader;
        _maximumChars = maximumChars;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder(Math.Min(_maximumChars, 4096));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == _count)
            {
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) return line.Length == 0 ? null : line.ToString();
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buffer[_offset] == '\n')
                {
                    _offset++;
                    continue;
                }
            }
            var start = _offset;
            while (_offset < _count && _buffer[_offset] is not ('\r' or '\n')) _offset++;
            var segmentLength = _offset - start;
            if (segmentLength > _maximumChars - line.Length)
                throw new InvalidDataException("Text line exceeds the permitted character limit.");
            line.Append(_buffer, start, segmentLength);
            if (_offset < _count)
            {
                _skipLf = _buffer[_offset++] == '\r';
                return line.ToString();
            }
        }
    }
}
