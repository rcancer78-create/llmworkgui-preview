using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Logging;

internal sealed class LoggerFactoryLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public LoggerFactoryLoggerProvider(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggerFactory.CreateLogger(categoryName);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }
}
