using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.OpenCode;

namespace LLMWorkGUI.Backends.OpenCode;

/// <summary>Owns the lazily started server whose advertised endpoint is shared by HTTP and SSE clients.</summary>
public sealed class OpenCodeServerConnection : IAsyncDisposable, IDisposable
{
    private readonly IOpenCodeServerManager _manager;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly TimeSpan _shutdownTimeout;
    private Task? _disposal;
    private IOpenCodeServerInstance? _current;
    private readonly Dictionary<string, IOpenCodeServerInstance> _profiles = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public OpenCodeServerConnection(IOpenCodeServerManager manager, IApplicationInstanceGuard? instanceGuard = null,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _instanceGuard = instanceGuard;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10);
        if (_shutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
    }

    public IOpenCodeServerInstance? Current => _disposed ? null : Volatile.Read(ref _current);

    public async Task<IOpenCodeServerInstance> GetInstanceAsync(string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        await GetBaseUrlAsync(providerProfileId, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var instance = _profiles[providerProfileId];
            if (!instance.IsAlive) throw new OpenCodeClientException("The managed OpenCode server is unavailable.");
            return instance;
        }
        finally { _gate.Release(); }
    }

    public async Task<Uri> GetBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instanceGuard?.EnsureSupervisorPermitted();
            if (_current is null)
            {
                _current = await _manager.StartServerAsync(cancellationToken).ConfigureAwait(false);
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            // Replacing a dead server could silently replace the instance behind a sticky session.
            if (!_current.IsAlive)
            {
                throw new OpenCodeClientException("The managed OpenCode server is unavailable; process termination may be unconfirmed. A replacement instance was not started.");
            }

            return _current.BaseUrl;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_disposeGate)
        {
            _disposed = true;
            // Keep ownership of delayed startup/cleanup even after the caller's bounded wait expires.
            if (_disposal is null || _disposal.IsFaulted || _disposal.IsCanceled)
                _disposal = DisposeCoreAsync();
            disposal = _disposal;
        }
        await disposal.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                await _manager.StopServerAsync(_current).ConfigureAwait(false);
                _current = null;
            }
            foreach (var instance in _profiles.Values.ToArray())
                await _manager.StopServerAsync(instance).ConfigureAwait(false);
            _profiles.Clear();
            if (_manager is OpenCodeServerManager ownedManager)
            {
                await ownedManager.WaitForRetainedStartupCleanupAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async Task<Uri> GetBaseUrlAsync(string providerProfileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerProfileId)
            || providerProfileId.Length > 128
            || providerProfileId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("A provider profile id is required.", nameof(providerProfileId));

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instanceGuard?.EnsureSupervisorPermitted();
            if (_profiles.TryGetValue(providerProfileId, out var existing))
            {
                if (!existing.IsAlive)
                    throw new OpenCodeClientException("The managed OpenCode server for this provider profile is unavailable; a replacement instance was not started.");
                return existing.BaseUrl;
            }

            var started = await _manager.StartServerAsync(cancellationToken).ConfigureAwait(false);
            if (_profiles.Values.Any(instance => instance.BaseUrl == started.BaseUrl)
                || (_current is not null && _current.BaseUrl == started.BaseUrl))
                throw new OpenCodeClientException("A provider profile received another profile's OpenCode server.");
            _profiles.Add(providerProfileId, started);
            return started.BaseUrl;
        }
        finally { _gate.Release(); }
    }
}
