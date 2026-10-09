using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Owns actual private runtimes. SQL metadata and public terminal projections cannot supply an owner.</summary>
public sealed partial class OpenCodeAdaptationRuntimeRegistry(
    ISqliteConnectionFactory factory, IApplicationInstanceGuard guard, TimeProvider clock,
    StorageOptions storage, IProviderProfileRepository profiles, IAccountRepository accounts,
    ISecretLifecycleService secrets, ISecretStore store, IOptions<OpenCodeServerOptions> options) : IAdaptationAccountConfigurationService, Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ConcurrentDictionary<Guid, Lease> _live = new();
    private readonly ConcurrentDictionary<Guid, Lease> _stoppedByThisRegistry = new();
    private readonly object _registrationGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _stopTask;
    private bool _stopping;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        lock (_registrationGate)
        {
            _stopping = true;
            stop = _stopTask ??= Task.Run(StopOwnedRuntimesAsync);
        }
        // A caller deadline ends its wait; it never fabricates native termination or releases a slot.
        return stop.WaitAsync(cancellationToken);
    }

    private async Task StopOwnedRuntimesAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var pending = 0;
        foreach (var lease in _live.Values.ToArray())
        {
            try { await StopAndReleaseAsync(lease).ConfigureAwait(false); }
            catch { pending++; }
        }
        if (pending != 0) throw new InvalidOperationException("Native runtime shutdown remains unconfirmed; durable ownership is retained.");
    }

    internal sealed class Lease(OpenCodeAdaptationRuntimeRegistry owner, Guid admission,
        AdaptationRouteIdentity identity, string fingerprint, string reference, string credentialHash,
        string executable, string executableHash, OpenCodeAdaptationRuntime runtime)
    {
        internal OpenCodeAdaptationRuntimeRegistry Owner { get; } = owner;
        internal Guid Admission { get; } = admission;
        internal AdaptationRouteIdentity Identity { get; } = identity;
        internal string Fingerprint { get; } = fingerprint;
        internal string Reference { get; } = reference;
        internal string CredentialHash { get; } = credentialHash;
        internal string Executable { get; } = executable;
        internal string ExecutableHash { get; } = executableHash;
        internal OpenCodeAdaptationRuntime Runtime { get; } = runtime;
        internal string SessionId => "adapt-session-" + Admission.ToString("N");
        internal string ExecutionId => "adapt-execution-" + Admission.ToString("N");
        internal string Query => "?directory=" + Uri.EscapeDataString(Runtime.WorkingDirectory);
        internal SemaphoreSlim CleanupGate { get; } = new(1, 1);
        internal bool CleanupRequested;
        internal bool Released;
        internal string TerminalState { get; private set; } = "Failed";
        internal void RecordCompletedTurn() => TerminalState = "Succeeded";
        internal void RecordCancelledTurn() => TerminalState = "Cancelled";
    }

    /// <summary>Explicit operator configuration, not auth/native account evidence. Uses only the account's stored key URN.</summary>
    public Task ConfigureAccountAsync(string accountId, string nativeProviderId, CancellationToken token = default)
        => ConfigureAccountCoreAsync(accountId, nativeProviderId, null, token);

    private async Task ConfigureAccountCoreAsync(string accountId, string nativeProviderId,
        AdaptationAccountConfiguration? expected, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        if (string.IsNullOrWhiteSpace(nativeProviderId) || nativeProviderId.Length > 128
            || nativeProviderId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw Refused();
        var account = await accounts.GetByIdAsync(accountId, token).ConfigureAwait(false) ?? throw Refused();
        var profile = await profiles.GetByIdAsync(account.ProviderProfileId, token).ConfigureAwait(false) ?? throw Refused();
        if (profile.Backend != LLMWorkGUI.Domain.Enums.BackendType.OpenCode || account.SecretReference is null) throw Refused();
        var key = await ResolveKeyAsync(account.Id, profile.Id, account.SecretReference, token).ConfigureAwait(false);
        if (key is null) throw Refused();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: false);
        if (expected is not null) await RequireConfigurationAsync(connection, tx, expected, token).ConfigureAwait(false);
        if (await HasOwnedExecutionAsync(connection, tx, accountId, token).ConfigureAwait(false)) throw Refused();
        await ExecuteAsync(connection, tx, """
            INSERT INTO OpenCodeAdaptationAccountMappings(AccountId,NativeProviderId,SecretReference,ConfiguredByInstanceId,ConfiguredAtUtc)
            VALUES($account,$provider,$reference,$actor,$now)
            ON CONFLICT(AccountId) DO UPDATE SET NativeProviderId=excluded.NativeProviderId,
                SecretReference=excluded.SecretReference,ConfiguredByInstanceId=excluded.ConfiguredByInstanceId,ConfiguredAtUtc=excluded.ConfiguredAtUtc;
            """, token, ("$account", accountId), ("$provider", nativeProviderId), ("$reference", account.SecretReference),
            ("$actor", guard.InstanceId), ("$now", clock.GetUtcNow().ToString("O"))).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested(); await tx.CommitAsync(token).ConfigureAwait(false);
    }

    internal async Task<Lease> StartAsync(AdaptationModelRequest request, AdaptationRouteIdentity identity,
        string fingerprint, CancellationToken token)
    {
        lock (_registrationGate) { if (_stopping) throw Refused(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = linked.Token;
        guard.EnsureSupervisorPermitted();
        if (request.AdmissionId is not { } admission || identity.Backend != LLMWorkGUI.Domain.Enums.BackendType.OpenCode
            || identity.BackendModelId is null || request.ModelId != identity.BackendModelId) throw Refused();
        var slash = identity.BackendModelId.IndexOf('/');
        if (slash < 1) throw Refused();
        var provider = identity.BackendModelId[..slash];
        var reference = await ReadMappingAsync(identity.AccountId, provider, token).ConfigureAwait(false);
        var account = await accounts.GetByIdAsync(identity.AccountId, token).ConfigureAwait(false) ?? throw Refused();
        var profile = await profiles.GetByIdAsync(identity.ProviderProfileId, token).ConfigureAwait(false) ?? throw Refused();
        if (account.ProviderProfileId != profile.Id || account.SecretReference != reference) throw Refused();
        var key = await ResolveKeyAsync(account.Id, profile.Id, reference, token).ConfigureAwait(false) ?? throw Refused();
        var executable = profile.ExecutablePath ?? options.Value.CustomExecutablePath;
        if (executable is null || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw Refused();
        executable = Path.GetFullPath(executable);
        var executableHash = FileHash(executable);
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetFullPath(storage.AppDataDirectory ?? AppDataPaths.DefaultRootDirectory),
            "scratch", "adaptation-runtime", id.ToString("N"));
        Lease? lease = null;
        OpenCodeAdaptationRuntime runtime;
        lock (_registrationGate)
        {
            if (_stopping || _live.Count >= 128 || _live.ContainsKey(admission)) throw Refused();
            Security.InputSanitizer.EnsureNoReparsePoints(Path.GetPathRoot(root)!, root);
            Directory.CreateDirectory(root);
            runtime = new OpenCodeAdaptationRuntime("adaptation-native-" + id.ToString("N"), executable, root,
                identity.BackendModelId, key, storage,
                (_, _, _, cancellation) => ReserveAsync(lease ?? throw Refused(), cancellation));
            lease = new Lease(this, admission, identity, fingerprint, reference, Hash(key), executable, executableHash, runtime);
            if (!_live.TryAdd(admission, lease)) throw Refused();
        }
        try
        {
            await runtime.StartAsync(token).ConfigureAwait(false);
            await MarkReadyAsync(lease, token).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            try { await StopAndReleaseAsync(lease).ConfigureAwait(false); }
            catch { /* Exact owner remains in the registry and durable reservation; no replacement or automatic launch. */ }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw Refused();
        }
    }

    internal async Task<Lease> RequireReadyAsync(Guid admission, string origin, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        lock (_registrationGate) { if (_stopping) throw Refused(); }
        if (!_live.TryGetValue(admission, out var lease) || lease.CleanupRequested || lease.Released
            || lease.Runtime.BaseUrl?.GetLeftPart(UriPartial.Authority) != origin) throw Refused();
        await CheckCredentialAsync(lease, token).ConfigureAwait(false);
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: false);
        await RequireOwnerAsync(connection, tx, lease, "Ready", token).ConfigureAwait(false);
        return lease;
    }

    internal async Task<IDisposable> PinOperationAsync(Lease lease, CancellationToken token)
    {
        if (!ReferenceEquals(lease.Owner, this)) throw Refused();
        await lease.CleanupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequirePrivateReady(lease);
            return new OperationPin(lease.CleanupGate);
        }
        catch { lease.CleanupGate.Release(); throw; }
    }

    /// <summary>Final in-memory authority check under the invoker's operation pin. Does not open a
    /// second SQLite writer while a transport transaction already owns one.</summary>
    internal void RequirePrivateReady(Lease lease)
    {
        guard.EnsureSupervisorPermitted();
        lock (_registrationGate)
        {
            if (_stopping) throw Refused();
            RequireLive(lease);
            if (lease.CleanupRequested || lease.Released || IsPhysicallyStopped(lease)
                || lease.Runtime.BaseUrl is null) throw Refused();
        }
    }

    private sealed class OperationPin(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal async Task StopAndReleaseAsync(Lease lease)
    {
        if (!ReferenceEquals(lease.Owner, this)) throw Refused();
        await lease.CleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (lease.Released) return;
            RequireLive(lease); lease.CleanupRequested = true;
            if (!IsPhysicallyStopped(lease))
            {
                await lease.Runtime.StopAsync().ConfigureAwait(false);
                // Unexposed registry membership follows the exact sealed owner await; consumers cannot supply a bool.
                if (!_stoppedByThisRegistry.TryAdd(lease.Admission, lease)) throw Refused();
            }
            await ReleaseAsync(lease).ConfigureAwait(false);
            lease.Released = true;
            _live.TryRemove(new KeyValuePair<Guid, Lease>(lease.Admission, lease));
            _stoppedByThisRegistry.TryRemove(new KeyValuePair<Guid, Lease>(lease.Admission, lease));
        }
        finally { lease.CleanupGate.Release(); }
    }

    internal void RequireAbortOwner(Guid admission, string origin)
    {
        guard.EnsureSupervisorPermitted();
        if (!_live.TryGetValue(admission, out var lease) || IsPhysicallyStopped(lease) || lease.Released
            || lease.Runtime.BaseUrl?.GetLeftPart(UriPartial.Authority) != origin) throw Refused();
    }

    private bool IsPhysicallyStopped(Lease lease) => _stoppedByThisRegistry.TryGetValue(lease.Admission, out var stopped)
        && ReferenceEquals(stopped, lease);

    /// <summary>Re-observes cleanup of retained objects from this instance only. Never starts/attaches/kills by persisted PID.</summary>
    public async Task<int> RetryOwnedCleanupAsync()
    {
        var pending = 0;
        foreach (var lease in _live.Values.Where(l => l.CleanupRequested).ToArray())
            try { await StopAndReleaseAsync(lease).ConfigureAwait(false); } catch { pending++; }
        return pending;
    }

    private void RequireLive(Lease lease)
    {
        if (!ReferenceEquals(lease.Owner, this) || !_live.TryGetValue(lease.Admission, out var actual)
            || !ReferenceEquals(actual, lease)) throw Refused();
    }

    private async Task<string?> ResolveKeyAsync(string accountId, string profileId, string reference, CancellationToken token)
    {
        var account = await accounts.GetByIdAsync(accountId, token).ConfigureAwait(false);
        if (account?.ProviderProfileId != profileId || account.SecretReference != reference) return null;
        var resolved = await ProviderRequestCredentials.ResolveApiKeyAsync(reference, profileId, accountId,
            profiles, accounts, secrets, store, token).ConfigureAwait(false);
        return resolved.Failure is null ? resolved.Secret : null;
    }
    private async Task CheckCredentialAsync(Lease lease, CancellationToken token)
    {
        var slash = lease.Identity.BackendModelId!.IndexOf('/');
        if (await ReadMappingAsync(lease.Identity.AccountId, lease.Identity.BackendModelId[..slash], token).ConfigureAwait(false) != lease.Reference
            || await ResolveKeyAsync(lease.Identity.AccountId, lease.Identity.ProviderProfileId, lease.Reference, token).ConfigureAwait(false) is not { } key
            || Hash(key) != lease.CredentialHash) throw Refused();
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FileHash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static WorkflowValidationException Refused() => new("Адаптация не передана: собственный runtime, выбранный API-key аккаунта или его native-провайдер не подтверждены. Неизвестное завершение сохраняет резерв.");
}
