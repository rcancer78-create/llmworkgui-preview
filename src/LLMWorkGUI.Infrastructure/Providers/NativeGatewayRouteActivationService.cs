using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Explicit first-use activation; neither inventory nor this consent is a passing model probe.</summary>
public sealed partial class NativeGatewayRouteActivationService(ISqliteConnectionFactory factory,
    Func<IAccountStore> accountStoreFactory, Func<IEnumerable<IProviderAdapter>> adaptersFactory,
    IApplicationInstanceGuard guard, TimeProvider clock) : INativeGatewayRouteActivationService
{
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private sealed record SharedState(bool ProviderReady, bool AccountReady, bool ModelReady,
        DataClassification ProfileClass, bool ProfileSibling, bool AccountSibling, bool ModelSibling,
        bool ScopedChatReady, bool SameBindingSibling);
    private sealed record Snapshot(NativeGatewayRouteBinding Binding, string Fingerprint, SharedState Shared);
    private sealed record Observation(string RouteId, string ExpectedEmail, Snapshot Snapshot,
        string NativeFingerprint, string ActualEmail, string NativeUserId, DateTimeOffset Expires);
    private static InvalidOperationException Refused() => new("Активация отклонена: обновите выбранный Cursor маршрут, проверьте вход и ограничения. Заблокированные маршруты автоматически не восстанавливаются.");
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    public async Task<NativeGatewayRouteActivationPreview> PreviewAsync(string routeId, string expectedEmail,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEmail);
        if (expectedEmail.Length > 254 || expectedEmail.Any(char.IsControl) || expectedEmail != expectedEmail.Trim()) throw Refused();
        var snapshot = await CaptureAsync(routeId, cancellationToken).ConfigureAwait(false);
        RejectSharedBroadening(snapshot.Shared, snapshot.Shared.ProfileClass);
        var observed = await ProbeAsync(snapshot.Binding, expectedEmail, cancellationToken).ConfigureAwait(false);
        if (snapshot != await CaptureAsync(routeId, cancellationToken).ConfigureAwait(false)) throw Refused();
        cancellationToken.ThrowIfCancellationRequested();
        guard.EnsureSupervisorPermitted();
        var now = clock.GetUtcNow();
        foreach (var entry in _observations.Where(item => item.Value.Expires <= now)) _observations.TryRemove(entry.Key, out _);
        if (_observations.Count >= 128) throw Refused();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(2);
        _observations[id] = new(routeId, expectedEmail, snapshot, observed.Fingerprint, observed.Email, observed.UserId, expires);
        return new(id, routeId, snapshot.Binding.ProviderProfileId, snapshot.Binding.AccountId,
            snapshot.Binding.ModelId, observed.Email, observed.UserId, snapshot.Binding.NativeModelId, expires);
    }

    public async Task<NativeGatewayRouteActivationResult> ActivateAsync(string observationId,
        bool confirmUserDeclaredModelSupport, bool allowUnverifiedFirstRequest, DataClassification maxDataClass,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmUserDeclaredModelSupport || !allowUnverifiedFirstRequest
            || maxDataClass is not (DataClassification.PublicSource or DataClassification.PrivateSource)
            || !_observations.TryRemove(observationId, out var observation) || observation.Expires <= clock.GetUtcNow()) throw Refused();
        if (observation.Snapshot != await CaptureAsync(observation.RouteId, cancellationToken).ConfigureAwait(false)) throw Refused();
        var actual = await ProbeAsync(observation.Snapshot.Binding, observation.ExpectedEmail, cancellationToken).ConfigureAwait(false);
        if (actual.Fingerprint != observation.NativeFingerprint || actual.Email != observation.ActualEmail || actual.UserId != observation.NativeUserId) throw Refused();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await CaptureAsync(connection, transaction, observation.RouteId, cancellationToken).ConfigureAwait(false);
        if (current != observation.Snapshot || observation.Expires <= clock.GetUtcNow()) throw Refused();
        RejectSharedBroadening(current.Shared, maxDataClass);
        await ApplyAsync(connection, transaction, observation.RouteId, current.Binding, maxDataClass, cancellationToken).ConfigureAwait(false);
        var nativeStore = accountStoreFactory();
        if (nativeStore.Find(current.Binding.NativeAccountId) is not { } latestProfile
            || Hash(latestProfile.Freeze()) != observation.NativeFingerprint) throw Refused();
        cancellationToken.ThrowIfCancellationRequested();
        guard.EnsureSupervisorPermitted();
        transaction.Commit();
        return new(observation.RouteId, actual.Email, current.Binding.NativeModelId, clock.GetUtcNow());
    }

    private static void RejectSharedBroadening(SharedState shared, DataClassification requestedClass)
    {
        if ((!shared.ProviderReady || requestedClass > shared.ProfileClass) && shared.ProfileSibling
            || !shared.AccountReady && shared.AccountSibling || !shared.ModelReady && shared.ModelSibling
            || !shared.ScopedChatReady && shared.SameBindingSibling)
            throw new InvalidOperationException("Активация одного маршрута изменит доступность или класс данных других включённых маршрутов. Настройте общую политику отдельно; другие маршруты не отключены.");
    }

    private async Task<(string Fingerprint, string Email, string UserId)> ProbeAsync(NativeGatewayRouteBinding binding, string expectedEmail, CancellationToken token)
    {
        // Direct native discovery: the aggregate gateway's refreshed catalog can retain stale/fallback inventory.
        var store = await Task.Run(accountStoreFactory, token).ConfigureAwait(false);
        var account = store.Find(binding.NativeAccountId)?.Freeze() ?? throw Refused();
        if (account.Provider != ProviderKind.Cursor || !account.Enabled || account.AuthMode != AccountAuthMode.NativeLogin) throw Refused();
        var fingerprint = Hash(account);
        var adapter = adaptersFactory().SingleOrDefault(item => item.Provider == ProviderKind.Cursor) ?? throw Refused();
        adapter.ValidateAccount(account);
        var status = await adapter.GetStatusAsync(account, token).ConfigureAwait(false);
        if (status.Availability != AccountAvailability.Ready || status.Identity?.Email is not { } email
            || !string.Equals(email, expectedEmail, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(status.Identity.ProviderAccountId)) throw Refused();
        var models = await adapter.ListModelsAsync(account, token).ConfigureAwait(false);
        if (!models.Any(model => string.Equals(model.Id, binding.NativeModelId, StringComparison.Ordinal))) throw Refused();
        var finalStatus = await adapter.GetStatusAsync(account, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (finalStatus.Availability != AccountAvailability.Ready || finalStatus.Identity != status.Identity
            || store.Find(account.Id) is not { } finalAccount || Hash(finalAccount.Freeze()) != fingerprint) throw Refused();
        return (fingerprint, email, status.Identity.ProviderAccountId);
    }
}
