using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

public sealed record OpenCodeTokenUsage(
    long Input,
    long Output,
    long Reasoning,
    long CacheRead,
    long CacheWrite)
{
    public static bool TryParse(JsonElement element, out OpenCodeTokenUsage? usage)
    {
        if (!OpenCodeJson.TryGetObjectProperty(element, "tokens", out var tokens))
        {
            usage = null;
            return false;
        }

        usage = null;
        if (!TryReadCounter(tokens, "input", out var input) ||
            !TryReadCounter(tokens, "output", out var output) ||
            !TryReadCounter(tokens, "reasoning", out var reasoning))
            return false;

        long cacheRead = 0, cacheWrite = 0;

        if (tokens.TryGetProperty("cache", out var cache))
        {
            if (cache.ValueKind != JsonValueKind.Object ||
                !TryReadCounter(cache, "read", out cacheRead) ||
                !TryReadCounter(cache, "write", out cacheWrite))
                return false;
        }

        usage = new OpenCodeTokenUsage(input, output, reasoning, cacheRead, cacheWrite);

        return true;
    }

    private static bool TryReadCounter(JsonElement element, string name, out long value)
    {
        value = 0;
        // Preserve sparse native accounting, but never turn a present malformed count into zero.
        if (!element.TryGetProperty(name, out _)) return true;
        if (OpenCodeJson.GetInt64(element, name) is not { } count || count < 0) return false;
        value = count;
        return true;
    }
}
