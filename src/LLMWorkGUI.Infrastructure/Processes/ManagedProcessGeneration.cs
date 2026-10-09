namespace LLMWorkGUI.Infrastructure.Processes;

internal static class ManagedProcessGeneration
{
    private static long _sequence;

    internal static long Next()
    {
        var value = Interlocked.Increment(ref _sequence);
        if (value <= 0) throw new InvalidOperationException("The managed process generation sequence is exhausted.");
        return value;
    }
}
