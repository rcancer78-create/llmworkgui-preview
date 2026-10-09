using System.Text;

namespace LLMWorkGUI.Infrastructure.Processes;

internal sealed class BoundedOutputCapture
{
    private readonly long _memoryLimitBytes;
    private readonly long _headLimitBytes;
    private readonly long _tailLimitBytes;
    private readonly List<Chunk> _headChunks = new();
    private readonly LinkedList<Chunk> _tailChunks = new();
    private long _headBytes;
    private long _tailBytes;
    private bool _overflowed;

    public BoundedOutputCapture(long memoryLimitBytes, long headLimitBytes, long tailLimitBytes)
    {
        _memoryLimitBytes = memoryLimitBytes;
        _headLimitBytes = headLimitBytes;
        _tailLimitBytes = tailLimitBytes;
    }

    public bool Overflowed => _overflowed;

    public long TotalBytes { get; private set; }

    public string HeadText => Concat(_headChunks);

    public string TailText => _overflowed ? Concat(_tailChunks) : string.Empty;

    public void Append(string text, int byteCount)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        TotalBytes += byteCount;

        if (!_overflowed)
        {
            _headChunks.Add(new Chunk(text, byteCount));
            _headBytes += byteCount;

            if (TotalBytes > _memoryLimitBytes)
            {
                _overflowed = true;
                Reorganize();
            }

            return;
        }

        _tailChunks.AddLast(new Chunk(text, byteCount));
        _tailBytes += byteCount;
        TrimTail();
    }

    private void Reorganize()
    {
        var captured = _headChunks.ToArray();
        _headChunks.Clear();
        _headBytes = 0;
        var headClosed = false;
        foreach (var chunk in captured)
        {
            if (!headClosed)
            {
                var prefix = Utf8Prefix(chunk.Text, _headLimitBytes - _headBytes);
                if (prefix.ByteCount > 0)
                {
                    _headChunks.Add(prefix);
                    _headBytes += prefix.ByteCount;
                }
                // A rune that cannot fit ends the prefix; later chunks cannot fill that gap.
                headClosed = prefix.Text.Length < chunk.Text.Length;
            }
            _tailChunks.AddLast(chunk);
            _tailBytes += chunk.ByteCount;
            TrimTail();
        }
    }

    private void TrimTail()
    {
        while (_tailBytes > _tailLimitBytes && _tailChunks.Count > 0)
        {
            var removed = _tailChunks.First!.Value;
            _tailChunks.RemoveFirst();
            _tailBytes -= removed.ByteCount;
            if (_tailBytes < _tailLimitBytes)
            {
                var suffix = Utf8Suffix(removed.Text, _tailLimitBytes - _tailBytes);
                if (suffix.ByteCount > 0)
                {
                    _tailChunks.AddFirst(suffix);
                    _tailBytes += suffix.ByteCount;
                }
            }
        }
    }

    private static Chunk Utf8Prefix(string text, long budget)
    {
        if (budget <= 0) return new Chunk(string.Empty, 0);
        var end = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > budget - bytes) break;
            bytes += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }
        return new Chunk(text[..end], bytes);
    }

    private static Chunk Utf8Suffix(string text, long budget)
    {
        if (budget <= 0) return new Chunk(string.Empty, 0);
        var start = text.Length;
        var bytes = 0;
        while (start > 0)
        {
            Rune.DecodeLastFromUtf16(text.AsSpan(0, start), out var rune, out var consumed);
            if (rune.Utf8SequenceLength > budget - bytes) break;
            bytes += rune.Utf8SequenceLength;
            start -= consumed;
        }
        return new Chunk(text[start..], bytes);
    }

    private static string Concat(ICollection<Chunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
        {
            builder.Append(chunk.Text);
        }

        return builder.ToString();
    }

    private readonly record struct Chunk(string Text, int ByteCount);
}
