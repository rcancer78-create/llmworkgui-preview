using System.Collections.Concurrent;
using System.Diagnostics;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.IntegrationTests.Processes;

internal sealed class ProcessTimingLog : ILogger<ProcessSupervisor>
{
    private readonly ConcurrentQueue<string> _messages = new();
    private TimeSpan? _completionElapsed;
    public TimeSpan CompletionElapsed => _completionElapsed
        ?? throw new InvalidOperationException("The measured operation has not completed.");

    // Measure the whole returned task (including cleanup), before xUnit's parallelism context
    // queues the test continuation. Await this wrapper so an absent measurement cannot pass.
    public Task<ProcessExecutionResult> MeasureCompletionAsync(Task<ProcessExecutionResult> operation, Stopwatch stopwatch)
    {
        return operation.ContinueWith(completed =>
        {
            _completionElapsed = stopwatch.Elapsed;
            RecordElapsed("task-completed", stopwatch);
            return completed.GetAwaiter().GetResult();
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void RecordElapsed(string phase, Stopwatch stopwatch) =>
        _messages.Enqueue($"Process test {phase}: {stopwatch.Elapsed.TotalMilliseconds} ms.");
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (message.StartsWith("Process timing ", StringComparison.Ordinal)) { _messages.Enqueue(message); }
    }
    public override string ToString() => string.Join(Environment.NewLine, _messages);
}
