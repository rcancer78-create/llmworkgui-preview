namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }
}
