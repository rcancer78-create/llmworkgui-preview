using LLMGateway.Core;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Ordinary project admission; conveys no Grok Bot consent, wire-hash or native-origin proof.</summary>
internal sealed class NativeGatewayDispatchAuthorization(NativeGatewayAdmission entry, SqliteNativeGatewayJournal journal, ICheckoutLockToken checkout)
    : INativeDispatchAuthorization
{
    private NativeChatRequest? _prepared;
    private int _claimed;
    private volatile bool _transportAttempted;
    private INativeProcessIdentity? _process;
    internal bool TransportAttempted => _transportAttempted;
    public bool RequiresProcessBinding => true;
    public DateTimeOffset ExpiresAtUtc => DateTimeOffset.MaxValue;

    private void EnsureCheckoutHeld()
    {
        if (!checkout.IsHeld || checkout.ExecutionId != entry.ExecutionId
            || !string.Equals(checkout.CanonicalRootPath, entry.Context.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Физическая блокировка проекта потеряна.");
        journal.EnsureSupervisorPermitted();
    }

    public async Task BindProcessAsync(INativeProcessIdentity process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        EnsureCheckoutHeld();
        if (_prepared is null || process.ProcessGeneration <= 0 || process.HasExited
            || Interlocked.CompareExchange(ref _process, process, null) is not null)
            throw new InvalidOperationException("Native process не принадлежит подготовленной отправке.");
        await journal.BindProcessAsync(entry, process.ProcessGeneration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCheckoutHeld();
        if (process.HasExited) throw new InvalidOperationException("Native process завершился до отправки.");
    }

    private void ValidateRequest(AccountProfile account, NativeChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureCheckoutHeld();
        if (account.Provider != entry.Context.Provider || account.Id != entry.Context.AccountId || !account.Enabled
            || request.Model != entry.Context.NativeModel || request.ReasoningEffort != entry.Context.ReasoningEffort
            || !ReferenceEquals(request.DispatchAuthorization, this)
            || !string.Equals(ProjectLock.CanonicalizeRoot(request.WorkingDirectory), entry.Context.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Привязка отправки LLMGateway изменилась.");
    }

    public async Task ValidatePreparedAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(account, request, cancellationToken);
        if (Interlocked.CompareExchange(ref _prepared, request, null) is not null)
            throw new InvalidOperationException("Повторная подготовка отправки LLMGateway запрещена.");
        await journal.ValidateDispatchAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task AuthorizeTransportAsync(AccountProfile account, NativeChatRequest request, string wirePrompt, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0 || !ReferenceEquals(_prepared, request) || wirePrompt != request.Prompt)
            throw new InvalidOperationException("Повторная или изменённая отправка LLMGateway запрещена.");
        ValidateRequest(account, request, cancellationToken);
        var process = _process ?? throw new InvalidOperationException("Native process не привязан до отправки.");
        if (process.HasExited) throw new InvalidOperationException("Native process завершился до отправки.");
        await journal.MarkRunningAsync(entry, cancellationToken, processGeneration: process.ProcessGeneration).ConfigureAwait(false);
        ValidateRequest(account, request, cancellationToken);
        EnsureCheckoutHeld();
        if (process.HasExited) throw new InvalidOperationException("Native process завершился до отправки.");
        _transportAttempted = true;
    }
}
