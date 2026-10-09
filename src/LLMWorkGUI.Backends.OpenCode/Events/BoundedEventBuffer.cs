using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

namespace LLMWorkGUI.Backends.OpenCode.Events;

public sealed class BoundedEventBuffer
{
    private readonly object _gate = new();
    private readonly OpenCodeEventEnvelope[] _items;
    private int _count;
    private bool _isOverflowed;

    public BoundedEventBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _items = new OpenCodeEventEnvelope[capacity];
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public bool IsOverflowed
    {
        get
        {
            lock (_gate)
            {
                return _isOverflowed;
            }
        }
    }

    public void Add(OpenCodeEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_gate)
        {
            if (_count < Capacity)
            {
                _items[_count] = envelope;
                _count++;
                return;
            }

            _isOverflowed = true;

            var evictionIndex = FindOldestNonTerminalIndex();

            if (!envelope.IsTerminal)
            {
                if (evictionIndex < 0)
                {
                    return;
                }
            }
            else if (evictionIndex < 0)
            {
                evictionIndex = 0;
            }

            for (var index = evictionIndex; index < _count - 1; index++)
            {
                _items[index] = _items[index + 1];
            }

            _items[_count - 1] = envelope;
        }
    }

    public IReadOnlyList<OpenCodeEventEnvelope> GetSnapshot(int? maxCount = null)
    {
        if (maxCount is <= 0)
        {
            return Array.Empty<OpenCodeEventEnvelope>();
        }

        lock (_gate)
        {
            var take = maxCount is { } limit ? Math.Min(limit, _count) : _count;
            var skip = _count - take;
            var snapshot = new OpenCodeEventEnvelope[take];

            for (var index = 0; index < take; index++)
            {
                snapshot[index] = _items[skip + index];
            }

            return snapshot;
        }
    }

    private int FindOldestNonTerminalIndex()
    {
        for (var index = 0; index < _count; index++)
        {
            if (!_items[index].IsTerminal)
            {
                return index;
            }
        }

        return -1;
    }
}
