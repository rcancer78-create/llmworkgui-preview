using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Logging;

public sealed class RedactingLogger : ILogger
{
    private static readonly IReadOnlyList<KeyValuePair<string, object?>> EmptyValues =
        Array.Empty<KeyValuePair<string, object?>>();

    private readonly ILogger _inner;
    private readonly SensitiveDataFilter _sensitiveDataFilter;

    public RedactingLogger(ILogger inner, SensitiveDataFilter sensitiveDataFilter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sensitiveDataFilter);

        _inner = inner;
        _sensitiveDataFilter = sensitiveDataFilter;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return _inner.BeginScope(RedactScope(state));
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
        var redactedValues = values is null ? EmptyValues : RedactValues(values);

        var message = formatter(state, exception);

        if (values is not null)
        {
            message = MaskSensitiveValues(values, message);
        }

        message = _sensitiveDataFilter.RedactDiagnostic(message);

        _inner.Log<object>(
            logLevel,
            eventId,
            new RedactedLogState(message, redactedValues),
            RedactException(exception),
            RedactedLogState.Format);
    }

    private string MaskSensitiveValues(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        string message)
    {
        foreach (var pair in values)
        {
            var text = pair.Value as string ?? pair.Value?.ToString();

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var cleaned = SensitiveDataFilter.IsSensitiveName(pair.Key)
                ? SensitiveDataFilter.Placeholder
                : RedactValue(pair)?.ToString() ?? string.Empty;

            if (!string.Equals(cleaned, text, StringComparison.Ordinal))
            {
                message = message.Replace(text, cleaned, StringComparison.Ordinal);
            }
        }

        return message;
    }

    private IReadOnlyList<KeyValuePair<string, object?>> RedactValues(
        IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var redactedValues = new List<KeyValuePair<string, object?>>(values.Count);

        foreach (var pair in values)
        {
            redactedValues.Add(new KeyValuePair<string, object?>(pair.Key, RedactValue(pair)));
        }

        return redactedValues;
    }

    private object? RedactValue(KeyValuePair<string, object?> pair)
    {
        if (SensitiveDataFilter.IsSensitiveName(pair.Key))
        {
            return SensitiveDataFilter.Placeholder;
        }

        return pair.Value switch
        {
            string text => _sensitiveDataFilter.RedactDiagnostic(text),
            null or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Enum => pair.Value,
            _ => RedactObject(pair.Value)
        };
    }

    private string RedactObject(object value)
    {
        try
        {
            return _sensitiveDataFilter.RedactJson(System.Text.Json.JsonSerializer.Serialize(value));
        }
        catch (Exception)
        {
            return SensitiveDataFilter.Placeholder;
        }
    }

    private object RedactScope(object state)
    {
        return state switch
        {
            string text => _sensitiveDataFilter.RedactDiagnostic(text),
            IReadOnlyList<KeyValuePair<string, object?>> values => RedactValues(values),
            _ => RedactObject(state)
        };
    }

    private Exception? RedactException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var message = RedactStructuredExceptionMessage(exception, out var structuredCredential);
        if (!_sensitiveDataFilter.ContainsSensitiveData(exception.ToString()) && !structuredCredential)
        {
            return exception;
        }

        return new RedactedException(
            exception.GetType().Name,
            message);
    }

    private string RedactStructuredExceptionMessage(Exception exception, out bool changed)
    {
        var message = _sensitiveDataFilter.RedactDiagnostic(exception.Message);
        changed = false;
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            var cleaned = _sensitiveDataFilter.RedactDiagnostic(current.Message);
            if (current.Data.Count > 0) changed = true;
            if (!string.Equals(current.Message, cleaned, StringComparison.Ordinal))
            {
                changed = true;
                // AggregateException.Message embeds its children's messages. Remove those exact
                // values before dropping the original exception tree, not only the InnerException.
                message = message.Replace(current.Message, cleaned, StringComparison.Ordinal);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return message;
    }
}
