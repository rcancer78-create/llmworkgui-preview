namespace LLMWorkGUI.IntegrationTests.Reconciliation;

internal sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }

    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }
}
