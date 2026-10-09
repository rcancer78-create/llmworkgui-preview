namespace LLMWorkGUI.IntegrationTests.Processes;

internal sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;

    public CallbackProgress(Action<T> onReport)
    {
        ArgumentNullException.ThrowIfNull(onReport);

        _onReport = onReport;
    }

    public void Report(T value)
    {
        _onReport(value);
    }
}
