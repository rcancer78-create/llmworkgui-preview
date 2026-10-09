using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Logging;

public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly SensitiveDataFilter _sensitiveDataFilter;
    private readonly ILoggerProvider _innerProvider;
    private bool _disposed;

    public RedactingLoggerProvider(SensitiveDataFilter sensitiveDataFilter, ILoggerProvider innerProvider)
    {
        ArgumentNullException.ThrowIfNull(sensitiveDataFilter);
        ArgumentNullException.ThrowIfNull(innerProvider);

        _sensitiveDataFilter = sensitiveDataFilter;
        _innerProvider = innerProvider;
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new RedactingLogger(_innerProvider.CreateLogger(categoryName), _sensitiveDataFilter);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _innerProvider.Dispose();
    }
}
