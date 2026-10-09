using System.Collections;

namespace LLMWorkGUI.Infrastructure.Logging;

internal sealed class RedactedLogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _values;

    public RedactedLogState(string message, IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        _values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public string Message { get; }

    public int Count => _values.Count;

    public KeyValuePair<string, object?> this[int index] => _values[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        return _values.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override string ToString()
    {
        return Message;
    }

    public static string Format(object state, Exception? exception)
    {
        return state is RedactedLogState redacted ? redacted.Message : state.ToString() ?? string.Empty;
    }
}
