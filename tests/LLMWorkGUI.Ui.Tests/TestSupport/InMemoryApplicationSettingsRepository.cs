using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class InMemoryApplicationSettingsRepository : IApplicationSettingsRepository
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Exception? GetException { get; set; }

    public Exception? SetException { get; set; }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? LastSetKey { get; private set; }

    public string? LastSetValue { get; private set; }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (GetException is not null)
        {
            throw GetException;
        }

        return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetValueAsync(
        string key,
        string value,
        string valueType = "String",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (SetException is not null)
        {
            throw SetException;
        }

        _values[key] = value;
        LastSetKey = key;
        LastSetValue = value;

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return Task.FromResult(_values.Remove(key));
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_values, StringComparer.Ordinal));
    }
}
