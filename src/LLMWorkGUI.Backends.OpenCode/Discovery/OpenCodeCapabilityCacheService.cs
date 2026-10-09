using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Health;

namespace LLMWorkGUI.Backends.OpenCode.Discovery;

public sealed class OpenCodeCapabilityCacheService : IOpenCodeCapabilityCacheService, IDisposable
{
    private readonly IOpenCodeClient _client;
    private readonly OpenCodeCapabilityCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IOpenCodeHealthEventSink? _healthEventSink;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();

    private IReadOnlyList<OpenCodeProviderInfo>? _providers;
    private IReadOnlyList<OpenCodeModelInfo>? _models;
    private DateTime _lastRefreshedAtUtc;
    private DateTime _expiresAtUtc;
    private long _generation;

    public OpenCodeCapabilityCacheService(
        IOpenCodeClient client,
        OpenCodeCapabilityCacheOptions? options = null,
        TimeProvider? timeProvider = null,
        IOpenCodeHealthEventSink? healthEventSink = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _options = options ?? new OpenCodeCapabilityCacheOptions();
        _options.Validate();

        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthEventSink = healthEventSink;
    }

    public async Task<IReadOnlyList<OpenCodeModelInfo>> GetModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        lock (_stateGate)
        {
            return _models ?? Array.Empty<OpenCodeModelInfo>();
        }
    }

    public async Task<IReadOnlyList<OpenCodeProviderInfo>> GetProvidersAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        lock (_stateGate)
        {
            return _providers ?? Array.Empty<OpenCodeProviderInfo>();
        }
    }

    public async Task<OpenCodeModelInfo?> GetModelAsync(
        string modelId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var models = await GetModelsAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        foreach (var model in models)
        {
            if (string.Equals(model.Id, modelId, StringComparison.Ordinal))
            {
                return model;
            }
        }

        return null;
    }

    public CapabilityFreshness GetFreshness()
    {
        lock (_stateGate)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            return new CapabilityFreshness
            {
                LastRefreshedAtUtc = _lastRefreshedAtUtc,
                ExpiresAtUtc = _expiresAtUtc,
                IsFresh = _providers is not null && now < _expiresAtUtc
            };
        }
    }

    public void Invalidate()
    {
        lock (_stateGate)
        {
            _generation++;
            _providers = null;
            _models = null;
            _lastRefreshedAtUtc = default;
            _expiresAtUtc = default;
        }
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
    }

    private bool TryGetFreshSnapshot()
    {
        lock (_stateGate)
        {
            return _providers is not null && _timeProvider.GetUtcNow().UtcDateTime < _expiresAtUtc;
        }
    }

    private async Task EnsureSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!forceRefresh && TryGetFreshSnapshot())
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!forceRefresh && TryGetFreshSnapshot())
            {
                return;
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        long generation;
        lock (_stateGate) { generation = _generation; }
        IReadOnlyList<OpenCodeProviderInfo> providers;
        IReadOnlyList<OpenCodeModelInfo> models;

        try
        {
            providers = await _client.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
            models = await _client
                .ListModelsAsync(providerId: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(exception, cancellationToken).ConfigureAwait(false);

            throw;
        }

        var refreshedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_stateGate)
        {
            if (generation != _generation) return;
            _providers = providers;
            _models = models;
            _lastRefreshedAtUtc = refreshedAtUtc;
            _expiresAtUtc = refreshedAtUtc + _options.Ttl;
        }
    }

    private async Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (_healthEventSink is null)
        {
            return;
        }

        try
        {
            await _healthEventSink
                .RecordHealthEventAsync(
                    _client.BaseUrl.ToString(),
                    $"OpenCode capability discovery failed: {exception.Message}",
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }
    }
}
