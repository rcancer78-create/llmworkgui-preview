using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Core;

public sealed class LlmGateway : ILlmGateway, IDisposable
{
    private readonly IAccountStore _store;
    private readonly IReadOnlyDictionary<ProviderKind, IProviderAdapter> _adapters;
    private readonly GatewayOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, AccountStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<NativeModel> Models)> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _quotas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Message, DateTimeOffset At, DateTimeOffset? RetryAt)> _observedLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly AccountRequestSlots _slots = new();
    private readonly AccountRequestSlots _probes = new();
    private readonly object _observationGate = new();
    private readonly object _catalogMissGate = new();
    private object _catalogMissGeneration = new();
    private Task? _catalogMissRefresh;
    private readonly Dictionary<string, object> _observationGenerations = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxConcurrentProbes = 4;

    public LlmGateway(IAccountStore store, IEnumerable<IProviderAdapter> adapters, GatewayOptions options, ILogger<LlmGateway>? logger = null)
    {
        _store = store;
        _adapters = adapters.ToDictionary(a => a.Provider);
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        foreach (var adapter in _adapters.Values.OfType<ICredentialEnvironmentAdapter>())
            adapter.BindCredentialEnvironment(store);
    }

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderInfo> result = _adapters.Values.OrderBy(a => a.Provider).Select(adapter =>
        {
            var path = adapter.ResolveExecutable(new AccountProfile { Provider = adapter.Provider });
            return new ProviderInfo(adapter.Provider, adapter.DisplayName, adapter.DefaultExecutable, path is not null, path, adapter.Capabilities);
        }).ToArray();
        return Task.FromResult(result);
    }

    /// <summary>Reference identity of the actually registered adapter, not provider/catalog metadata.</summary>
    public bool HasRegisteredAdapter(IProviderAdapter adapter) =>
        _adapters.TryGetValue(adapter.Provider, out var registered) && ReferenceEquals(registered, adapter);

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AccountInfo> result = _store.GetAll().Select(ToInfo).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Freeze();
        EnsureAdapter(profile.Provider).ValidateAccount(profile);
        await _store.AddAsync(profile, cancellationToken).ConfigureAwait(false);
        Invalidate(profile.Id);
        return ToInfo(Require(profile.Id));
    }

    public async Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Freeze();
        EnsureAdapter(profile.Provider).ValidateAccount(profile);
        await _store.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        Invalidate(profile.Id);
        return ToInfo(Require(profile.Id));
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _store.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
        Invalidate(accountId);
        _slots.Retire(accountId);
    }

    public async Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _store.SelectAsync(accountId, cancellationToken).ConfigureAwait(false);
        return ToInfo(Require(accountId));
    }

    public async Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = Require(accountId);
        await RefreshStatusAsync(account, cancellationToken).ConfigureAwait(false);
        return ToInfo(account);
    }

    public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = Require(accountId);
        _statuses.TryRemove(account.Id, out _);
        return Adapter(account).StartInteractiveLoginAsync(account, cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var accounts = Select(accountId).Where(a => a.Enabled && _adapters.TryGetValue(a.Provider, out var adapter) && adapter.ResolveExecutable(a) is not null).ToList();
        var ttl = TimeSpan.FromSeconds(_options.ModelCacheSeconds);
        await Task.WhenAll(accounts.Select(async account =>
        {
            if (!refresh && _models.TryGetValue(account.Id, out var cached) && DateTimeOffset.UtcNow - cached.At < ttl) return;
            var observation = CaptureObservation(account);
            using var probe = await _probes.AcquireAsync("catalog-and-quota", MaxConcurrentProbes, cancellationToken).ConfigureAwait(false);
            var adapter = Adapter(account);
            try
            {
                var models = await adapter.ListModelsAsync(account, cancellationToken).ConfigureAwait(false);
                lock (_observationGate)
                    if (models.Count > 0 && IsCurrentObservation(account.Id, observation))
                        _models[account.Id] = (DateTimeOffset.UtcNow, models);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Model discovery failed for {Provider} ({ExceptionType})", account.Provider, ex.GetType().Name);
            }
        })).ConfigureAwait(false);

        return accounts.SelectMany(account =>
        {
            var models = _models.TryGetValue(account.Id, out var cached) ? cached.Models : Fallback(account);
            return models.Select(m => new GatewayModel(ModelRouter.ModelId(account, m.Id), m.Id, m.DisplayName, account.Provider, account.Id, m.IsDefault));
        }).ToArray();
    }

    public async Task<AuthenticatedModelOptions> DiscoverModelOptionsAsync(string accountId, string nativeModel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = Require(accountId).Freeze();
        if (!account.Enabled || !ModelRouter.IsValidModelName(nativeModel) || nativeModel == "auto")
            throw GatewayException.Invalid("An enabled account and explicit native model are required.");
        if (Adapter(account) is not IAuthenticatedModelOptionsAdapter discovery)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Native client does not report authenticated per-model options.");
        var observation = CaptureObservation(account);
        if (observation is null) throw GatewayException.Invalid("Native account changed before discovery.");
        using var probe = await _probes.AcquireAsync("catalog-and-quota", MaxConcurrentProbes, cancellationToken).ConfigureAwait(false);
        var report = await discovery.DiscoverModelOptionsAsync(account, nativeModel, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_observationGate)
        {
            if (!IsCurrentObservation(account.Id, observation) || report.AccountId != account.Id
                || report.Provider != account.Provider || report.NativeModel != nativeModel)
                throw GatewayException.Invalid("Native account or model changed during discovery.");
            return report with { ReasoningEfforts = Array.AsReadOnly(report.ReasoningEfforts.ToArray()) };
        }
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var accounts = Select(accountId).ToList();
        var ttl = TimeSpan.FromSeconds(_options.QuotaCacheSeconds);
        var snapshots = await Task.WhenAll(accounts.Select(async account =>
        {
            if (!refresh && _quotas.TryGetValue(account.Id, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < ttl)
                return Overlay(account, cached);
            var observation = CaptureObservation(account);
            using var probe = await _probes.AcquireAsync("catalog-and-quota", MaxConcurrentProbes, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadQuotaAsync(account, observation, cancellationToken).ConfigureAwait(false);
            lock (_observationGate)
                if (!snapshot.IsStale && IsCurrentObservation(account.Id, observation)) _quotas[account.Id] = snapshot;
            return Overlay(account, snapshot);
        })).ConfigureAwait(false);
        return snapshots;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (update.Kind == ChatUpdateKind.Completed) return update.Result!;
        throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент завершился без ответа.");
    }

    public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count == 0) throw GatewayException.Invalid("Поле messages обязательно.");
        if (request.ResponseFormat?.Kind == ResponseFormatKind.JsonSchema)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Проверка JSON Schema не поддерживается нативными клиентами.");
        var route = await RouteAsync(request, cancellationToken).ConfigureAwait(false);
        var account = route.Account;
        // Capture the routed profile before preparation/dispatch awaits. Account mutations retire
        // observations from this generation, while the admitted request retains its own lifetime.
        var chatObservation = CaptureObservation(account);
        var adapter = Adapter(account);
        adapter.ValidateAccount(account);
        if (request.DispatchAuthorization is not null
            && adapter is not INativeDispatchAuthorizationAdapter { SupportsNativeDispatchAuthorization: true })
            throw GatewayException.Invalid("Адаптер не поддерживает подтверждённую границу отправки проекта.");
        if (!adapter.Capabilities.SupportsToolCalling &&
            (request.Tools is { Count: > 0 } || request.ToolChoice is not null
             || request.Messages.Any(m => m.Role == ChatRole.Tool || m.ToolCalls is { Count: > 0 } || m.ToolCallId is not null)))
            throw GatewayException.Invalid("Этот ограниченный провайдер не поддерживает вызовы инструментов; рекомендуется текстовое ревью.");
        if (!adapter.Capabilities.SupportsStructuredOutput && request.ResponseFormat?.Kind is ResponseFormatKind.JsonObject or ResponseFormatKind.JsonSchema)
            throw GatewayException.Invalid("Этот ограниченный провайдер не гарантирует структурированный ответ.");
        if (!adapter.Capabilities.SupportsReasoningEffort && request.ReasoningEffort is not null)
            throw GatewayException.Invalid("Этот провайдер не поддерживает выбор reasoning effort.");
        if (adapter.ResolveExecutable(account) is null)
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, $"{adapter.DisplayName}: нативный клиент '{account.Executable ?? adapter.DefaultExecutable}' не установлен.");
        if (route.NativeModel is not null && !ModelRouter.IsValidModelName(route.NativeModel))
            throw GatewayException.Invalid($"Недопустимое имя модели '{route.NativeModel}'.");

        var prompt = PromptBuilder.Build(request, adapter.Capabilities.MaxPromptCharacters);
        var useTools = ToolCalling.IsEnabled(request);
        var jsonMode = request.ResponseFormat?.Kind is ResponseFormatKind.JsonObject or ResponseFormatKind.JsonSchema;
        var buffered = useTools || jsonMode;
        var workingDirectory = request.ExecutionContext is { } context
            ? context.WorkingDirectory : WorkingDirectory(account);
        var modelId = route.NativeModel is null
            ? $"{account.Provider.ToString().ToLowerInvariant()}/{account.Id}"
            : ModelRouter.ModelId(account, route.NativeModel);
        var timeoutSpan = TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds));
        var nativeRequest = new NativeChatRequest
        {
            Prompt = prompt, Model = route.NativeModel, ReasoningEffort = request.ReasoningEffort,
            WorkingDirectory = workingDirectory, Timeout = timeoutSpan, DispatchAuthorization = request.DispatchAuthorization
        };
        using var slot = await _slots.AcquireAsync(account.Id, _options.MaxConcurrentRequestsPerAccount, cancellationToken).ConfigureAwait(false);
        await using var preparation = await adapter.PrepareRequestAsync(account, nativeRequest, cancellationToken).ConfigureAwait(false);
        if (nativeRequest.DispatchAuthorization is { } authorization)
            await authorization.ValidatePreparedAsync(account, nativeRequest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatUpdate(ChatUpdateKind.Started, AccountId: account.Id, Provider: account.Provider, Model: modelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutSpan);
        var text = new StringBuilder();
        var stop = new StopSequenceFilter(useTools ? null : request.Stop);
        TokenUsage? usage = null;
        string? finalText = null;
        var truncated = false;
        IAsyncEnumerator<NativeChatEvent>? events = null;
        var providerFailed = false;
        try
        {
        events = adapter.RunChatAsync(account, nativeRequest, timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    providerFailed = true;
                    throw new GatewayException(GatewayErrorKind.Timeout, $"Нативный клиент не ответил за {timeoutSpan.TotalSeconds:0} с.");
                }
                catch
                {
                    providerFailed = true;
                    throw;
                }
                if (!moved) break;
                var item = events.Current;
                switch (item.Kind)
                {
                    case NativeChatEventKind.Text when !string.IsNullOrEmpty(item.Text):
                        var safe = stop.Push(item.Text);
                        var piece = OutputBudget.Fit(safe, text.Length, request.MaxOutputTokens, out var exhausted);
                        if (piece.Length > 0)
                        {
                            text.Append(piece);
                            if (!buffered) yield return new ChatUpdate(ChatUpdateKind.TextDelta, piece);
                        }
                        truncated |= exhausted;
                        break;
                    case NativeChatEventKind.Reasoning when !string.IsNullOrEmpty(item.Text):
                        yield return new ChatUpdate(ChatUpdateKind.ReasoningDelta, item.Text);
                        break;
                    case NativeChatEventKind.FinalText:
                        finalText = item.Text;
                        break;
                    case NativeChatEventKind.Usage:
                        usage = item.Usage;
                        break;
                    case NativeChatEventKind.Error:
                        providerFailed = true;
                        RecordFailure(account, item, chatObservation);
                        throw new GatewayException(item.ErrorKind, $"{adapter.DisplayName}: {NativeErrorClassifier.Trim(item.Text)}") { RetryAt = item.RetryAt };
                }
                if (stop.Stopped || truncated) break;
            }
        }
        finally
        {
            if (events is not null)
            {
                try { await events.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (providerFailed)
                {
                    // Preserve the primary provider failure; cleanup-only failures still prevent success.
                    _logger.LogWarning("Native chat cleanup failed ({ExceptionType})", ex.GetType().Name);
                }
            }
        }

        if (!truncated)
        {
            var tail = stop.Flush();
            var piece = OutputBudget.Fit(tail, text.Length, request.MaxOutputTokens, out var exhausted);
            truncated = exhausted;
            if (piece.Length > 0)
            {
                text.Append(piece);
                if (!buffered) yield return new ChatUpdate(ChatUpdateKind.TextDelta, piece);
            }
        }
        if (!truncated && text.Length == 0 && !string.IsNullOrEmpty(finalText))
        {
            var final = OutputBudget.Fit(stop.Push(finalText) + stop.Flush(), text.Length, request.MaxOutputTokens, out var exhausted);
            truncated = exhausted;
            text.Append(final);
            if (!buffered && final.Length > 0) yield return new ChatUpdate(ChatUpdateKind.TextDelta, final);
        }

        var content = text.ToString();
        IReadOnlyList<ToolCall> calls = [];
        if (!truncated && useTools)
        {
            (content, calls) = ToolCalling.Parse(content, request.Tools!);
            ToolCalling.ValidateResult(request, calls);
            var visibleTextStop = new StopSequenceFilter(request.Stop);
            content = visibleTextStop.Push(content) + visibleTextStop.Flush();
        }
        if (!truncated && jsonMode && calls.Count == 0) content = PromptBuilder.StripJsonFences(content);
        if (request.ResponseFormat?.Kind == ResponseFormatKind.JsonObject && calls.Count == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул ответ, не являющийся JSON-объектом.");
            }
            catch (JsonException ex)
            {
                throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректный JSON-объект.", ex);
            }
        }
        if (buffered && content.Length > 0) yield return new ChatUpdate(ChatUpdateKind.TextDelta, content);
        if (!truncated && content.Length == 0 && calls.Count == 0)
            throw new GatewayException(GatewayErrorKind.Upstream, $"{adapter.DisplayName}: пустой ответ нативного клиента.");

        RecordSuccess(account, chatObservation);
        var finish = truncated ? "length" : calls.Count > 0 ? "tool_calls" : "stop";
        if (truncated && request.MaxOutputTokens is > 0 and var cap && usage is { } reported)
            usage = reported with { CompletionTokens = Math.Min(reported.CompletionTokens, cap), TotalTokens = reported.PromptTokens + Math.Min(reported.CompletionTokens, cap) };
        var result = new ChatResult(
            "chatcmpl-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            modelId,
            route.NativeModel ?? "default",
            account.Provider,
            account.Id,
            content.Length == 0 ? null : content,
            calls,
            finish,
            usage ?? TokenUsage.Estimate(prompt, content));
        yield return new ChatUpdate(ChatUpdateKind.Completed, Result: result, AccountId: account.Id, Provider: account.Provider, Model: modelId);
    }

    private async Task<RouteResult> RouteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var accounts = _store.GetAll();
        if (request.ExecutionContext is { } context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(context.Provider) || context.Provider == ProviderKind.Unknown || string.IsNullOrWhiteSpace(context.AccountId)
                || !ModelRouter.IsValidModelName(context.NativeModel) || request.ReasoningEffort != context.ReasoningEffort
                || context.NativeModel.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(context.WorkingDirectory) || !Directory.Exists(context.WorkingDirectory))
                throw GatewayException.Invalid("Некорректная привязка выполнения LLMGateway.");
            var matches = accounts.Where(a => a.Id.Equals(context.AccountId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Provider != context.Provider || !matches[0].Enabled)
                throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Привязанный аккаунт LLMGateway недоступен.");
            if (matches[0].ExtraArguments.Count != 0)
                throw GatewayException.Invalid("Дополнительные CLI-аргументы не поддерживаются для привязанного выполнения.");
            var expectedModel = ModelRouter.ModelId(matches[0], context.NativeModel);
            if (request.AccountId != context.AccountId || request.Model != expectedModel)
                throw GatewayException.Invalid("Маршрут запроса не совпадает с привязкой выполнения.");
            return new RouteResult(matches[0], context.NativeModel);
        }
        IReadOnlyList<NativeModel>? Cached(string id) => _models.TryGetValue(id, out var value) ? value.Models : null;
        bool AllowAutomatic(ProviderKind kind) => _adapters.TryGetValue(kind, out var adapter) && adapter.Capabilities.SupportsAutomaticRouting;
        object catalogGeneration;
        lock (_catalogMissGate) catalogGeneration = _catalogMissGeneration;
        try
        {
            return ModelRouter.Resolve(request, accounts, _options.DefaultProvider, Cached, AllowAutomatic);
        }
        catch (GatewayException ex) when (ex.Kind == GatewayErrorKind.ModelNotFound)
        {
            await RefreshCatalogAfterMissAsync(catalogGeneration, cancellationToken).ConfigureAwait(false);
            accounts = _store.GetAll();
            return ModelRouter.Resolve(request, accounts, _options.DefaultProvider, Cached, AllowAutomatic);
        }
    }

    private Task RefreshCatalogAfterMissAsync(object generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task refresh;
        TaskCompletionSource? owner = null;
        lock (_catalogMissGate)
        {
            // Requests that missed the same observation share its refresh, including waiters
            // whose continuation runs after that refresh has already finished.
            if (!ReferenceEquals(generation, _catalogMissGeneration)) return Task.CompletedTask;
            if (_catalogMissRefresh is not null) refresh = _catalogMissRefresh;
            else
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                refresh = _catalogMissRefresh = owner.Task;
            }
        }
        // Discovery captures account observations outside this gate; invalidation takes
        // the observation gate before this gate and must never encounter a reverse order.
        if (owner is not null) _ = RunCatalogMissRefreshAsync(generation, owner);
        // A request can cancel its wait without cancelling discovery needed by another request.
        return refresh.WaitAsync(cancellationToken);
    }

    private async Task RunCatalogMissRefreshAsync(object generation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await GetModelsAsync(true, null, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        lock (_catalogMissGate)
        {
            // An old-profile refresh must not retire a newer profile's active refresh.
            if (ReferenceEquals(generation, _catalogMissGeneration))
            {
                _catalogMissGeneration = new object();
                _catalogMissRefresh = null;
            }
        }
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }

    private async Task<QuotaSnapshot> ReadQuotaAsync(AccountProfile account, Observation? observation, CancellationToken cancellationToken)
    {
        var adapter = Adapter(account);
        if (!account.Enabled)
            return QuotaSnapshot.Unsupported(account, AccountAvailability.Disabled, null, "gateway", "Аккаунт отключён.");
        if (adapter.ResolveExecutable(account) is null)
            return QuotaSnapshot.Unsupported(account, AccountAvailability.NotInstalled, null, "gateway", $"Клиент '{account.Executable ?? adapter.DefaultExecutable}' не установлен.");
        try
        {
            var snapshot = await adapter.GetQuotaAsync(account, cancellationToken).ConfigureAwait(false);
            lock (_observationGate)
            if (IsCurrentObservation(account.Id, observation)) _statuses.AddOrUpdate(account.Id,
                _ => PreserveObservedLimitOnReady(account, new AccountStatus(snapshot.Availability, snapshot.Plan is null ? null : new AccountIdentity(null, snapshot.Plan, null), null, snapshot.Message, DateTimeOffset.UtcNow)),
                (_, old) => PreserveObservedLimitOnReady(account, old with
                {
                    Availability = snapshot.Availability == AccountAvailability.Unknown ? old.Availability : snapshot.Availability,
                    Message = snapshot.Availability == AccountAvailability.Unknown ? old.Message : snapshot.Message,
                    CheckedAt = DateTimeOffset.UtcNow
                }));
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Quota read failed for {Provider} ({ExceptionType})", account.Provider, ex.GetType().Name);
            return _quotas.TryGetValue(account.Id, out var old)
                ? old with { IsStale = true, Message = NativeErrorClassifier.Trim(ex.Message) }
                : new QuotaSnapshot(account.Id, account.Provider, DateTimeOffset.UtcNow, AccountAvailability.Error, adapter.Capabilities.SupportsQuota, null, [], adapter.Capabilities.QuotaSource, NativeErrorClassifier.Trim(ex.Message));
        }
    }

    private QuotaSnapshot Overlay(AccountProfile account, QuotaSnapshot snapshot)
    {
        lock (_observationGate)
        {
            if (!_observedLimits.TryGetValue(account.Id, out var observed)) return snapshot;
            if (observed.RetryAt is { } retry && retry <= DateTimeOffset.UtcNow)
            {
                _observedLimits.TryRemove(account.Id, out _);
                // Only retire the public state that still represents this overlay. A later
                // authentication/error observation or a real quota limit remains authoritative.
                if (snapshot.Availability == AccountAvailability.Ready
                    && _statuses.TryGetValue(account.Id, out var current)
                    && current.Availability == AccountAvailability.RateLimited
                    && string.Equals(current.Message, observed.Message, StringComparison.Ordinal))
                    _statuses[account.Id] = current with
                    { Availability = AccountAvailability.Ready, Message = snapshot.Message, CheckedAt = snapshot.FetchedAt };
                return snapshot;
            }
            var buckets = snapshot.Buckets.ToList();
            buckets.Add(new QuotaBucket("observed", "Лимит, сообщённый клиентом", 100, ResetsAt: observed.RetryAt));
            return snapshot with { Availability = AccountAvailability.RateLimited, Buckets = buckets, Message = observed.Message };
        }
    }

    // Caller holds _observationGate. A Ready probe does not revoke a still-active native limit;
    // unavailable/authentication/error states retain their stronger diagnostic meaning.
    private AccountStatus PreserveObservedLimitOnReady(AccountProfile account, AccountStatus status)
    {
        if (status.Availability != AccountAvailability.Ready
            || !_observedLimits.TryGetValue(account.Id, out var observed)) return status;
        if (observed.RetryAt is { } retry && retry <= DateTimeOffset.UtcNow)
        {
            _observedLimits.TryRemove(account.Id, out _);
            return status;
        }
        return status with { Availability = AccountAvailability.RateLimited, Message = observed.Message };
    }

    private async Task RefreshStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var observation = CaptureObservation(account);
        var adapter = Adapter(account);
        AccountStatus status;
        if (!account.Enabled) status = new AccountStatus(AccountAvailability.Disabled, null, null, "Аккаунт отключён.", DateTimeOffset.UtcNow);
        else if (adapter.ResolveExecutable(account) is null)
            status = new AccountStatus(AccountAvailability.NotInstalled, null, null, $"Клиент '{account.Executable ?? adapter.DefaultExecutable}' не найден в PATH.", DateTimeOffset.UtcNow);
        else
        {
            try { status = await adapter.GetStatusAsync(account, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                status = new AccountStatus(AccountAvailability.Error, null, null, NativeErrorClassifier.Trim(ex.Message), DateTimeOffset.UtcNow);
            }
        }
        lock (_observationGate)
            if (IsCurrentObservation(account.Id, observation))
                _statuses[account.Id] = PreserveObservedLimitOnReady(account, status);
    }

    private void RecordFailure(AccountProfile account, NativeChatEvent failure, Observation? observation)
    {
        var message = NativeErrorClassifier.Trim(failure.Text);
        lock (_observationGate)
        {
            if (IsCurrentObservation(account.Id, observation))
            {
                if (failure.ErrorKind == GatewayErrorKind.RateLimited)
                {
                    _observedLimits[account.Id] = (message, DateTimeOffset.UtcNow, failure.RetryAt);
                    _statuses[account.Id] = new AccountStatus(AccountAvailability.RateLimited, Status(account)?.Identity, null, message, DateTimeOffset.UtcNow);
                }
                else if (failure.ErrorKind == GatewayErrorKind.AuthenticationRequired)
                {
                    _statuses[account.Id] = new AccountStatus(AccountAvailability.AuthenticationRequired, null, null, message, DateTimeOffset.UtcNow);
                }
            }
        }
        _logger.LogWarning("Native chat failed for {Provider}: {Kind}", account.Provider, failure.ErrorKind);
    }

    private void RecordSuccess(AccountProfile account, Observation? observation)
    {
        lock (_observationGate)
        {
            if (!IsCurrentObservation(account.Id, observation)) return;
            // Another already-admitted request may have reported a newer limit. Completion of
            // this request proves its own success, not restoration of the account's quota.
            if (_observedLimits.TryGetValue(account.Id, out var observed)
                && (observed.RetryAt > DateTimeOffset.UtcNow
                    || observed.RetryAt is null && observation!.AcceptedAt <= observed.At))
            {
                _statuses.AddOrUpdate(account.Id,
                    _ => new AccountStatus(AccountAvailability.RateLimited, null, null, observed.Message, DateTimeOffset.UtcNow),
                    (_, old) => old with { Availability = AccountAvailability.RateLimited, Message = observed.Message, CheckedAt = DateTimeOffset.UtcNow });
                return;
            }
            _observedLimits.TryRemove(account.Id, out _);
            MarkReady(account);
        }
    }

    private void MarkReady(AccountProfile account) =>
        _statuses.AddOrUpdate(account.Id,
            _ => new AccountStatus(AccountAvailability.Ready, null, null, null, DateTimeOffset.UtcNow),
            (_, old) => old with { Availability = AccountAvailability.Ready, Message = null, CheckedAt = DateTimeOffset.UtcNow });

    private AccountInfo ToInfo(AccountProfile account)
    {
        var adapter = Adapter(account);
        var status = Status(account);
        var availability = status?.Availability
            ?? (!account.Enabled ? AccountAvailability.Disabled
                : adapter.ResolveExecutable(account) is null ? AccountAvailability.NotInstalled : AccountAvailability.Unknown);
        return new AccountInfo(account.Id, account.DisplayName, account.Provider, account.IsActive, account.Enabled, availability,
            status?.Identity, account.Executable ?? adapter.DefaultExecutable, account.ConfigDirectory, account.AuthMode,
            account.ApiKeyVariable, account.WorkingDirectory, account.DefaultModel, status?.Message, status?.CheckedAt,
            AccountEnvironment.WithoutSecrets(account.Environment), account.ExtraArguments);
    }

    private AccountStatus? Status(AccountProfile account) => _statuses.TryGetValue(account.Id, out var status) ? status : null;

    private IEnumerable<NativeModel> Fallback(AccountProfile account) =>
        account.DefaultModel is { Length: > 0 } model ? [new NativeModel(model, model, true)] : [new NativeModel("auto", "По умолчанию клиента", true)];

    private string WorkingDirectory(AccountProfile account)
    {
        var directory = string.IsNullOrWhiteSpace(account.WorkingDirectory) ? _options.ResolveWorkspace() : account.WorkingDirectory!;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private IEnumerable<AccountProfile> Select(string? accountId) => string.IsNullOrWhiteSpace(accountId)
        ? _store.GetAll()
        : [Require(accountId)];

    private AccountProfile Require(string accountId) => _store.Find(accountId)
        ?? throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{accountId}' не найден.");

    private IProviderAdapter Adapter(AccountProfile account) => EnsureAdapter(account.Provider);


    private IProviderAdapter EnsureAdapter(ProviderKind provider) => _adapters.TryGetValue(provider, out var adapter)
        ? adapter
        : throw new GatewayException(GatewayErrorKind.Unsupported, $"Адаптер для {provider} не зарегистрирован.");

    private void Invalidate(string accountId)
    {
        lock (_observationGate)
        {
            _observationGenerations.Remove(accountId);
            _statuses.TryRemove(accountId, out _);
            _models.TryRemove(accountId, out _);
            _quotas.TryRemove(accountId, out _);
            _observedLimits.TryRemove(accountId, out _);
            lock (_catalogMissGate)
            {
                _catalogMissGeneration = new object();
                _catalogMissRefresh = null;
            }
        }
    }

    private sealed record Observation(object Generation, string Profile, string? CredentialFingerprint, DateTimeOffset AcceptedAt);

    private static string? CredentialFingerprint(AccountProfile account) => account.AuthMode != AccountAuthMode.ApiKeyFromEnvironment
        ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            (string.IsNullOrWhiteSpace(account.ApiKeyVariable) ? null : Environment.GetEnvironmentVariable(account.ApiKeyVariable)) ?? "")));

    private Observation? CaptureObservation(AccountProfile account)
    {
        lock (_observationGate)
        {
            var current = _store.Find(account.Id);
            var profile = JsonSerializer.Serialize(account, GatewayJson.Options);
            if (current is null || JsonSerializer.Serialize(current, GatewayJson.Options) != profile) return null;
            if (!_observationGenerations.TryGetValue(account.Id, out var generation))
                _observationGenerations[account.Id] = generation = new object();
            return new Observation(generation, profile, CredentialFingerprint(account), DateTimeOffset.UtcNow);
        }
    }

    // Caller holds _observationGate so a successful mutation cannot invalidate between validation and publication.
    private bool IsCurrentObservation(string accountId, Observation? observation) =>
        observation is not null && _observationGenerations.TryGetValue(accountId, out var generation)
        && ReferenceEquals(generation, observation.Generation) && _store.Find(accountId) is { } current
        && JsonSerializer.Serialize(current, GatewayJson.Options) == observation.Profile
        && CredentialFingerprint(current) == observation.CredentialFingerprint;

    public void Dispose()
    {
        _slots.Dispose();
        _probes.Dispose();
    }
}
