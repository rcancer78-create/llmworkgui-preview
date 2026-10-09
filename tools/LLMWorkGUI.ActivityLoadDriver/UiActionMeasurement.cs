using System.Diagnostics;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>The measured action and its settled description share one report outcome.</summary>
internal static class UiActionMeasurement
{
    public static async Task<UiActionResult> MeasureAsync(
        string name,
        Func<string> action,
        Func<string>? describe,
        Func<Func<string>, Task<string>> invokeUi,
        Func<Task> settle,
        Action<Exception> reportFailure)
    {
        await settle();
        var watch = Stopwatch.StartNew();
        string note;
        try
        {
            note = await invokeUi(action);
        }
        catch (Exception exception)
        {
            note = $"NOT PERFORMED: {exception.GetType().Name}: {exception.Message}";
            reportFailure(exception);
        }
        watch.Stop();
        await settle();
        if (describe is not null)
        {
            try
            {
                var observed = await invokeUi(describe);
                note = note.StartsWith("NOT PERFORMED", StringComparison.Ordinal)
                    ? $"{note} (observed state: {observed})"
                    : observed;
            }
            catch (Exception exception)
            {
                note = $"{note} (observed state unavailable: {exception.GetType().Name}: {exception.Message})";
            }
        }
        return new UiActionResult(name, watch.Elapsed.TotalMilliseconds, note);
    }
}
