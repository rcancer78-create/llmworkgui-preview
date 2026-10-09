using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.IntegrationTests;

internal sealed class CapturedLogEntry
{
    public LogLevel LogLevel { get; init; }

    public EventId EventId { get; init; }

    public object? State { get; init; }

    public Exception? Exception { get; init; }

    public string Message { get; init; } = string.Empty;
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<CapturedLogEntry> Entries { get; } = new();

    public List<object?> Scopes { get; } = new();

    public bool IsDisposed { get; private set; }

    public bool IsEnabled { get; set; } = true;

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(this);
    }

    public void Dispose()
    {
        IsDisposed = true;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _provider;

        public CapturingLogger(CapturingLoggerProvider provider)
        {
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            _provider.Scopes.Add(state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return _provider.IsEnabled;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _provider.Entries.Add(new CapturedLogEntry
            {
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                Message = formatter(state, exception)
            });
        }
    }
}
