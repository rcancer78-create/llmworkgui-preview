using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMWorkGUI.Application.Reviews;
using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.GrokBot;

/// <summary>Explicit user-requested Grok Bot compatibility integration; separate from Grok Build CLI.</summary>
public sealed class GrokBotProviderAdapter(IGrokBotReviewTransport transport, ExecutableResolver resolver) : IProviderAdapter, INativeDispatchAuthorizationAdapter
{
    bool INativeDispatchAuthorizationAdapter.SupportsNativeDispatchAuthorization => true;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly ConcurrentDictionary<NativeChatRequest, Preparation> _prepared = new();
    public const string Notice = GrokBotRestrictions.Notice;
    internal bool HasRegisteredTransport(IGrokBotReviewTransport expected) =>
        ReferenceEquals(transport, expected) && transport is GrokBotReviewTransport;
    public ProviderKind Provider => ProviderKind.GrokBot;
    public string DisplayName => "Grok Bot · ограниченный, для ревью";
    public string DefaultExecutable => "node";
    public ProviderCapabilities Capabilities { get; } = new(false, "Not reported", MultiAccountSupport.Single,
        "Интеграция отключена; используйте официальный клиент Cursor.",
        null, null, false, GrokBotRestrictions.MaxPromptCharacters)
    {
        IsLimited = true, Limitations = Notice, SupportsToolCalling = false,
        SupportsStructuredOutput = false, SupportsAutomaticRouting = false
    };
    public string? ResolveExecutable(AccountProfile account) => resolver.Resolve("node") is { Kind: LaunchKind.Direct } target
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "GrokBot", "review-bridge.mjs")) ? target.FileName : null;
    public async Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        Validate(account);
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ResolveExecutable(account);
        if (executable is null)
            return new(AccountAvailability.NotInstalled, null, null, "Нужен Node.js >=20.3 и установленный компонент Grok Bot.", DateTimeOffset.UtcNow);
        try
        {
            var ready = await transport.CheckSessionAsync(executable, cancellationToken).ConfigureAwait(false);
            return new(ready ? AccountAvailability.Ready : AccountAvailability.AuthenticationRequired, null, null,
                Notice, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(AccountAvailability.AuthenticationRequired, null, null, Notice, DateTimeOffset.UtcNow); }
    }
    public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        Validate(account); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<NativeModel>>([new("grok-bot", "Grok Bot · ревью (модель не подтверждена)", true)]);
    }
    public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken) =>
        Task.FromResult(QuotaSnapshot.Unsupported(account, AccountAvailability.Unknown, null, "Not reported", "Лимит проверяется в Grok Bot; адаптер не сообщает квоты."));
    public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken) =>
        throw new GatewayException(GatewayErrorKind.Unsupported, Notice);
    public IEnumerable<AccountProfile> DiscoverProfiles() => [];
    public void ValidateAccount(AccountProfile account) => Validate(account);
    public async ValueTask<IAsyncDisposable?> PrepareRequestAsync(AccountProfile account, NativeChatRequest request,
        CancellationToken cancellationToken)
    {
        Validate(account);
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ResolveExecutable(account)
            ?? throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Node.js или компонент Grok Bot не установлен.");
        if (request.Model is not (null or "grok-bot") || request.ReasoningEffort is not null)
            throw GatewayException.Invalid("Grok Bot поддерживает только алиас grok-bot без выбора effort.");
        var wirePrompt = GrokBotPromptEnvelope.Build(request.Prompt);
        if (!await _slot.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Grok Bot занят другим запросом; автоматического повтора нет.");
        var preparation = new Preparation(this, request, executable, wirePrompt);
        if (!_prepared.TryAdd(request, preparation))
        {
            _slot.Release();
            throw GatewayException.Invalid("Запрос Grok Bot уже подготовлен.");
        }
        return preparation;
    }
    public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_prepared.TryGetValue(request, out var preparation))
            throw new InvalidOperationException("Нарушен инвариант владения подготовленным запросом Grok Bot; автоматический повтор запрещён.");
        if (!preparation.TryClaim())
            throw new InvalidOperationException("Подготовленный запрос Grok Bot уже использован; автоматический повтор запрещён.");
        Validate(account);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DispatchAuthorization is { } authorization)
            await authorization.AuthorizeTransportAsync(account, request, preparation.WirePrompt, cancellationToken).ConfigureAwait(false);
        var reply = request.DispatchAuthorization is { } gate
            ? await transport.ReviewAsync(preparation.Executable, preparation.WirePrompt, gate.ExpiresAtUtc, cancellationToken).ConfigureAwait(false)
            : await transport.ReviewAsync(preparation.Executable, preparation.WirePrompt, cancellationToken).ConfigureAwait(false);
        var text = reply.Text;
        if (reply.CleanupPending)
            text += "\n\n[LLMWorkGUI: очистка временного агента Grok Bot не подтверждена; проверьте его в Grok Bot.]";
        yield return NativeChatEvent.Final(text);
    }
    private sealed class Preparation(GrokBotProviderAdapter owner, NativeChatRequest request, string executable, string wirePrompt) : IAsyncDisposable
    {
        private GrokBotProviderAdapter? _owner = owner;
        private int _claimed;
        public string Executable { get; } = executable;
        public string WirePrompt { get; } = wirePrompt;
        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) { current._prepared.TryRemove(request, out _); current._slot.Release(); }
            return ValueTask.CompletedTask;
        }
    }
    private static void Validate(AccountProfile account)
    {
        if (account.Provider != ProviderKind.GrokBot || account.Id != "grokbot-default" || account.AuthMode != AccountAuthMode.NativeLogin
            || account.ConfigDirectory is not null || account.ApiKeyVariable is not null
            || account.Executable is not null || account.Environment is not { Count: 0 } || account.ExtraArguments is not { Count: 0 }
            || account.DefaultModel is not (null or "grok-bot"))
            throw GatewayException.Invalid("Grok Bot использует только grokbot-default и текущий desktop-вход; overrides и дополнительные аккаунты не поддерживаются.");
    }
}
