using LLMWorkGUI.Backends.OpenCode.Events;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeSseResourceBoundaryTests
{
    [Fact]
    public async Task OversizedUnterminatedLineStopsReadingWithinTheLineBudget()
    {
        using var reader = new GeneratedLineReader(1024 * 1024 + 32000);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new OpenCodeSseParser().ParseAsync(reader)) { }
        });
        Assert.InRange(reader.ReadCharacters, 1, 1024 * 1024 + 4096);
    }

    [Fact]
    public async Task MultilineEventCannotAccumulateBeyondItsBudget()
    {
        var payload = string.Join("\n", Enumerable.Repeat("data: " + new string('x', 256 * 1024), 17));
        using var reader = new StringReader(payload);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new OpenCodeSseParser().ParseAsync(reader)) { }
        });
    }

    private sealed class GeneratedLineReader(int length) : TextReader
    {
        public int ReadCharacters { get; private set; }
        public override int Read() => ReadCharacters >= length ? -1 : Next();
        private char Next()
        {
            var offset = ReadCharacters++;
            return offset < 6 ? "data: "[offset] : 'x';
        }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, length - ReadCharacters);
            for (var i = 0; i < count; i++) buffer.Span[i] = Next();
            return ValueTask.FromResult(count);
        }
    }
}
