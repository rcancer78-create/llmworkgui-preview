using System.Buffers;
using System.Text;

namespace LLMWorkGUI.Infrastructure.Http;

internal static class BoundedHttpContent
{
    public static async Task<string> ReadTextAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("HTTP response exceeds the permitted byte limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, maximumBytes - body.Length + 1)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > maximumBytes)
                    throw new InvalidDataException("HTTP response exceeds the permitted byte limit.");
                body.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        Encoding encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException exception) { throw new InvalidDataException("Unsupported HTTP text encoding.", exception); }
        }
        body.Position = 0;
        using var reader = new StreamReader(body, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
