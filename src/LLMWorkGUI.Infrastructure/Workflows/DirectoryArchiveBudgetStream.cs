using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Bounds actual archive length while preserving the underlying spool's seek/ownership contract.</summary>
internal sealed class DirectoryArchiveBudgetStream(Stream inner, long maximumLength) : Stream
{
    private WorkflowValidationException? _refusal;

    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set { CheckPosition(value); inner.Position = value; }
    }

    private void CheckPosition(long value)
    {
        if (_refusal is not null) throw _refusal;
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value > maximumLength)
        {
            _refusal = new WorkflowValidationException("Generated archive exceeds the maximum allowed archive size.");
            throw _refusal;
        }
    }

    private void CheckWrite(int count)
    {
        if (_refusal is not null) throw _refusal;
        var position = inner.Position;
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (position > maximumLength || count > maximumLength - position)
        {
            _refusal = new WorkflowValidationException("Generated archive exceeds the maximum allowed archive size.");
            throw _refusal;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Buffer range is invalid.");
        CheckWrite(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckWrite(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(inner.Position + offset),
            SeekOrigin.End => checked(inner.Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        CheckPosition(target);
        return inner.Seek(offset, origin);
    }
    public override void SetLength(long value) { CheckPosition(value); inner.SetLength(value); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    // The caller retains the real FileStream and owns its flush/disposal. Disposing this wrapper cannot
    // close it early; any archive finalizer after the first refusal encounters the same sticky guard.
}
