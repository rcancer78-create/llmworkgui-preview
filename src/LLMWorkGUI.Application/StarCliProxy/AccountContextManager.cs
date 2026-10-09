using System.Collections.Concurrent;
using LLMWorkGUI.Application.Agy;

namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Default account context manager (ADR-0007, ТЗ §6.4, §6.11a). Codex contexts are registered
/// immutable absolute CODEX_HOME directories; AGY contexts delegate to <see cref="IAgyProfileService"/>,
/// which owns the no-<c>-Force</c>, no-<c>next</c>/<c>random</c> and no-live-process invariants.
/// All select → verify → launch sequences run under one semaphore, so an account switch cannot
/// interleave with another switch or launch (TOCTOU-free).
/// </summary>
public sealed class AccountContextManager : IAccountContextManager
{
    private readonly IAgyProfileService _agyProfileService;
    private readonly ConcurrentDictionary<string, CodexAccountContext> _codexContexts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _accountLock = new(initialCount: 1, maxCount: 1);
    private readonly object _codexRegistryLock = new();
    private static readonly StringComparer CodexHomeComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public AccountContextManager(IAgyProfileService agyProfileService)
    {
        ArgumentNullException.ThrowIfNull(agyProfileService);

        _agyProfileService = agyProfileService;
    }

    public IReadOnlyList<CodexAccountContext> CodexContexts =>
        _codexContexts.Values
            .OrderBy(context => context.AccountId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool HasPinnableContexts =>
        _agyProfileService.IsAvailable ||
        _codexContexts.Values.Any(context =>
            Path.IsPathFullyQualified(context.CodexHomePath) && Directory.Exists(context.CodexHomePath));

    public void RegisterCodexContext(CodexAccountContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_codexRegistryLock)
        {
            if (_codexContexts.TryGetValue(context.AccountId, out var registered))
            {
                if (!CodexHomeComparer.Equals(registered.CodexHomePath, context.CodexHomePath))
                    throw new InvalidOperationException("A registered Codex account cannot be remapped to another CODEX_HOME.");
                return; // Repeated configuration of the same immutable identity is idempotent.
            }
            if (_codexContexts.Values.Any(existing => CodexHomeComparer.Equals(existing.CodexHomePath, context.CodexHomePath)))
                throw new InvalidOperationException("Each Codex account requires a separate CODEX_HOME directory.");
            _codexContexts[context.AccountId] = context;
        }
    }

    public async Task<IReadOnlyList<AgyAccountContext>> ListAgyContextsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_agyProfileService.IsAvailable)
        {
            return Array.Empty<AgyAccountContext>();
        }

        var profiles = await _agyProfileService.ListProfilesAsync(cancellationToken).ConfigureAwait(false);

        return profiles
            .Where(profile => AgyProfilePolicy.IsValidProfileName(profile.Name))
            .Select(profile => new AgyAccountContext(profile.Name))
            .ToArray();
    }

    public async Task<string?> GetActiveAgyProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!_agyProfileService.IsAvailable)
        {
            return null;
        }

        return await _agyProfileService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountContextSelection> SelectAndVerifyAsync(
        AccountContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A direct select → verify may switch the AGY profile, so it must hold the same
        // account lock as ExecuteSerializedAsync (ADR-0007 §3, TOCTOU-free).
        await _accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await SelectAndVerifyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountLock.Release();
        }
    }

    public async Task<AccountContextSelection> VerifyContextAsync(
        AccountContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read-only observation must still wait for managed switches to finish so list/current
        // cannot describe two different contexts within one verification.
        await _accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return request.Kind switch
            {
                AccountContextKind.Codex => SelectCodex(request.AccountId),
                AccountContextKind.Agy => await SelectAgyAsync(request.AccountId, switchWhenNeeded: false, cancellationToken).ConfigureAwait(false),
                _ => AccountContextSelection.Unresolved(request.Kind, request.AccountId,
                    $"Unsupported account context kind '{request.Kind}'.")
            };
        }
        finally { _accountLock.Release(); }
    }

    public async Task<TResult> ExecuteSerializedAsync<TResult>(
        AccountContextRequest request,
        Func<AccountContextSelection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);

        await _accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // SelectAndVerifyCoreAsync is used directly: SemaphoreSlim is not reentrant, and
            // SelectAndVerifyAsync would deadlock under the already held lock.
            var selection = await SelectAndVerifyCoreAsync(request, cancellationToken).ConfigureAwait(false);

            return await operation(selection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountLock.Release();
        }
    }

    private Task<AccountContextSelection> SelectAndVerifyCoreAsync(
        AccountContextRequest request,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            AccountContextKind.Codex => Task.FromResult(SelectCodex(request.AccountId)),
            AccountContextKind.Agy => SelectAgyAsync(
                request.AccountId,
                switchWhenNeeded: true,
                cancellationToken),
            _ => Task.FromResult(AccountContextSelection.Unresolved(
                request.Kind,
                request.AccountId,
                $"Unsupported account context kind '{request.Kind}'."))
        };
    }

    private AccountContextSelection SelectCodex(string accountId)
    {
        if (!_codexContexts.TryGetValue(accountId, out var context))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Codex,
                accountId,
                $"{StarCliProxyPolicy.CodexHomeRequiredReason} Requested account: '{accountId}'.");
        }

        if (!Path.IsPathFullyQualified(context.CodexHomePath))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Codex,
                accountId,
                $"CODEX_HOME '{context.CodexHomePath}' is not an absolute path; relative or implicit homes are forbidden (ТЗ §6.11a).");
        }

        if (!Directory.Exists(context.CodexHomePath))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Codex,
                accountId,
                $"The CODEX_HOME directory '{context.CodexHomePath}' does not exist. Create and authenticate it " +
                "independently before use; contexts are never copied or created implicitly (ADR-0007 §4).");
        }

        return AccountContextSelection.ResolvedCodex(context);
    }

    private async Task<AccountContextSelection> SelectAgyAsync(
        string profileName,
        bool switchWhenNeeded,
        CancellationToken cancellationToken)
    {
        if (!_agyProfileService.IsAvailable)
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                _agyProfileService.AvailabilityBlocker ?? AgyProfilePolicy.NotInstalledBlocker);
        }

        if (!AgyProfilePolicy.IsValidProfileName(profileName))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                $"AGY profile name '{profileName}' is invalid: only letters, digits, '-' and '_' are allowed.");
        }

        var profiles = await _agyProfileService.ListProfilesAsync(cancellationToken).ConfigureAwait(false);

        if (!profiles.Any(profile => string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                $"AGY profile '{profileName}' is not a saved agy-profile profile; profiles are never created or " +
                "rotated implicitly (ТЗ §6.11a).");
        }

        var activeProfile = await _agyProfileService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(activeProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return AccountContextSelection.ResolvedAgy(profileName, requiresNewSession: false);
        }

        if (!switchWhenNeeded)
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                $"AGY profile '{profileName}' is not the active profile ('{activeProfile ?? "(none)"}'). " +
                "A non-active profile cannot be verified without switching accounts.");
        }

        var switchResult = await _agyProfileService
            .SwitchProfileAsync(profileName, cancellationToken)
            .ConfigureAwait(false);

        if (!switchResult.IsSwitched)
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                switchResult.FailureReason ?? $"Switching to AGY profile '{profileName}' failed.");
        }

        if (!string.Equals(switchResult.ActiveProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return AccountContextSelection.Unresolved(
                AccountContextKind.Agy,
                profileName,
                $"AGY profile switch was not confirmed: requested '{profileName}', " +
                $"observed '{switchResult.ActiveProfile ?? "(none)"}'.");
        }

        // Invariant (ТЗ §6.11a): changing the account context requires a new native session;
        // no previous conversation/session id may be carried to the new profile.
        return AccountContextSelection.ResolvedAgy(profileName, requiresNewSession: true);
    }
}
