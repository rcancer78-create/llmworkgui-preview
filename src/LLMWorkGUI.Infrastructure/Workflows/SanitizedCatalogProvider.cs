using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Builds a read-only, credential-free projection of the locally known providers and models.
/// The three local repositories are the account/profile/health source; the optional
/// <see cref="IProviderModelCatalogSource"/> contributes the locally configured backend model ids.
/// No network discovery, no quota refresh and no secret material ever enters the catalog (ТЗ §6.14).
/// </summary>
public sealed partial class SanitizedCatalogProvider : ISanitizedCatalogProvider
{
    private const string AccountScopeType = "account";

    private readonly IProviderProfileRepository _providerProfileRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IHealthStateRepository _healthStateRepository;
    private readonly IProviderModelCatalogSource? _modelCatalogSource;
    private readonly TimeProvider _timeProvider;
    private readonly SensitiveDataFilter _sensitiveDataFilter;
    private readonly IModelRouteConfigurationService? _configuration;

    public SanitizedCatalogProvider(
        IProviderProfileRepository providerProfileRepository,
        IAccountRepository accountRepository,
        IHealthStateRepository healthStateRepository,
        IProviderModelCatalogSource? modelCatalogSource = null,
        TimeProvider? timeProvider = null,
        SensitiveDataFilter? sensitiveDataFilter = null,
        IModelRouteConfigurationService? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(providerProfileRepository);
        ArgumentNullException.ThrowIfNull(accountRepository);
        ArgumentNullException.ThrowIfNull(healthStateRepository);

        _providerProfileRepository = providerProfileRepository;
        _accountRepository = accountRepository;
        _healthStateRepository = healthStateRepository;
        _modelCatalogSource = modelCatalogSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sensitiveDataFilter = sensitiveDataFilter ?? new SensitiveDataFilter();
        _configuration = configuration;
    }

    public async Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var profiles = await _providerProfileRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var accounts = await _accountRepository
            .ListAllAsync(cancellationToken)
            .ConfigureAwait(false);

        var healthStates = await _healthStateRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var healthByAccountId = BuildAccountHealthMap(healthStates);
        var enabledProviderIds = new HashSet<string>(
            profiles.Where(profile => profile.IsEnabled).Select(profile => profile.Id),
            StringComparer.Ordinal);
        var backendByProfileId = new Dictionary<string, BackendType>(StringComparer.Ordinal);

        foreach (var profile in profiles)
        {
            backendByProfileId[profile.Id] = profile.Backend;
        }

        var providers = profiles
            .Select(profile => new SanitizedProviderInfo(
                SanitizeIdentifier(profile.Id),
                SanitizeText(profile.DisplayName),
                profile.Backend,
                profile.IsEnabled))
            .OrderBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();

        // One settings read per profile and backend, however many accounts that profile owns.
        var configuredModelsByProfile = new Dictionary<(string ProfileId, BackendType Backend), IReadOnlyList<string>>();

        var models = new List<SanitizedModelInfo>(accounts.Count);
        var configuration = _configuration is null ? null
            : await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var account in accounts)
        {
            models.Add(await ProjectModelAsync(
                    account,
                    enabledProviderIds,
                    healthByAccountId,
                    backendByProfileId,
                    configuredModelsByProfile,
                    configuration,
                    now,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return new SanitizedCapabilityCatalog(
            providers,
            models.OrderBy(model => model.ModelId, StringComparer.Ordinal).ToArray(),
            now);
    }

    /// <summary>
    /// Resolves the backend model ids that are really configured for one account. The local provider
    /// configuration wins, because a normal OpenCode account record carries no <c>ProviderNativeId</c>;
    /// the account record is consulted only when the configuration has none, only for a backend that
    /// can run adaptation, and only when its value can pass <see cref="BackendModelIdPolicy"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveBackendModelIdsAsync(
        Account account,
        BackendType? backend,
        Dictionary<(string ProfileId, BackendType Backend), IReadOnlyList<string>> configuredModelsByProfile,
        CancellationToken cancellationToken)
    {
        if (backend is not { } resolvedBackend)
        {
            return Array.Empty<string>();
        }

        var cacheKey = (account.ProviderProfileId, resolvedBackend);

        if (!configuredModelsByProfile.TryGetValue(cacheKey, out var configured))
        {
            configured = await ReadConfiguredModelIdsAsync(account, resolvedBackend, cancellationToken)
                .ConfigureAwait(false);

            configuredModelsByProfile[cacheKey] = configured;
        }

        if (configured.Count > 0)
        {
            return configured;
        }

        // The account record is only a model source for a backend that actually takes a model in a
        // request. StarCliProxy stores a Codex home path and Agy a profile name in the very same
        // column, and a bare profile name is lexically indistinguishable from a model id — so on those
        // backends the record value is structurally not a model and is never offered as one.
        if (!AdaptationRouteIdentity.IsAdaptationCapableBackend(resolvedBackend))
        {
            return Array.Empty<string>();
        }

        return SanitizedModelId.TryNormalize(_sensitiveDataFilter, account.ProviderNativeId, out var nativeModelId)
            ? new[] { nativeModelId }
            : Array.Empty<string>();
    }

    private async Task<IReadOnlyList<string>> ReadConfiguredModelIdsAsync(
        Account account,
        BackendType backend,
        CancellationToken cancellationToken)
    {
        if (_modelCatalogSource is null)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<ProviderModelDescriptor> models;

        try
        {
            models = await _modelCatalogSource
                .ListModelsAsync(new ProviderModelCatalogQuery(account.ProviderProfileId, backend), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A model source that cannot answer leaves the account without a configured model: the
            // route is then refused instead of being invented from the account record.
            return Array.Empty<string>();
        }

        var accepted = new List<string>(models.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in models)
        {
            if (SanitizedModelId.TryNormalize(_sensitiveDataFilter, model.ModelId, out var modelId)
                && seen.Add(modelId))
            {
                accepted.Add(modelId);
            }
        }

        return accepted;
    }

    private static Dictionary<string, HealthState> BuildAccountHealthMap(
        IReadOnlyList<HealthStateRecord> healthStates)
    {
        var map = new Dictionary<string, HealthState>(StringComparer.Ordinal);

        foreach (var record in healthStates)
        {
            if (!string.Equals(record.ScopeType, AccountScopeType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!map.ContainsKey(record.ScopeId))
            {
                map[record.ScopeId] = record.State;
            }
        }

        return map;
    }

    private async Task<SanitizedModelInfo> ProjectModelAsync(
        Account account,
        HashSet<string> enabledProviderIds,
        Dictionary<string, HealthState> healthByAccountId,
        Dictionary<string, BackendType> backendByProfileId,
        Dictionary<(string ProfileId, BackendType Backend), IReadOnlyList<string>> configuredModelsByProfile,
        ModelRouteConfiguration? configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var modelId = SanitizeIdentifier(account.Id);
        var displayName = SanitizeText(account.DisplayName);

        var health = healthByAccountId.TryGetValue(account.Id, out var recordedHealth)
            ? recordedHealth
            : account.Health;

        var isRoutable = enabledProviderIds.Contains(account.ProviderProfileId)
            && account.IsEligibleForRouting(now);

        BackendType? backend = backendByProfileId.TryGetValue(account.ProviderProfileId, out var foundBackend)
            ? foundBackend : null;

        var backendModelIds = await ResolveBackendModelIdsAsync(
                account,
                backend,
                configuredModelsByProfile,
                cancellationToken)
            .ConfigureAwait(false);

        // The route identity is published as separate fields: ModelId stays the catalog key (the
        // local account id) and never doubles as a backend model id, while the selectable
        // backend-native model ids come from the local provider configuration, or from the account
        // record when the configuration has none.
        var capabilities = backendModelIds.ToDictionary(id => id,
            id => ResolveCapabilities(configuration, account, backend, id, now), StringComparer.Ordinal);
        var common = CommonCapabilities(capabilities.Values.ToArray());
        return new SanitizedModelInfo(
            modelId,
            displayName,
            common.Flags,
            common.ReasoningEfforts,
            common.SpeedModes,
            common.ContextLimit,
            health,
            isRoutable)
        {
            AccountId = modelId,
            ProviderProfileId = SanitizeIdentifier(account.ProviderProfileId),
            Backend = backend,
            BackendCapabilities = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SanitizedModelCapabilities>(capabilities),
            BackendModelIds = backendModelIds,
            BackendModelId = backendModelIds.Count > 0 ? SanitizeIdentifier(backendModelIds[0]) : null
        };
    }

    private static SanitizedModelCapabilities ResolveCapabilities(ModelRouteConfiguration? configuration,
        Account account, BackendType? backend, string nativeId, DateTimeOffset now)
    {
        if (configuration is null || backend is null) return SanitizedModelCapabilities.Unknown;
        var model = configuration.Models.SingleOrDefault(item => item.ProfileId == account.ProviderProfileId
            && item.Backend == backend && item.NativeModelId == nativeId && item.IsEnabled
            && item.Capability == CapabilityState.Supported);
        if (model is null || !configuration.Routes.Any(route => route.IsEnabled && route.ModelId == model.Id
            && route.AccountId == account.Id && route.ProfileId == account.ProviderProfileId && route.Backend == backend))
            return SanitizedModelCapabilities.Unknown;
        var evidence = configuration.Capabilities.SingleOrDefault(item => item.ModelId == model.Id && item.AccountId == account.Id);
        if (evidence is null || !evidence.IsCurrent(now)) return SanitizedModelCapabilities.Unknown;
        var reasoning = backend == BackendType.CursorAcp && evidence.Provenance == ModelProvenance.UserDefined
            ? Array.Empty<string>() : evidence.ReasoningEfforts;
        var speed = backend == BackendType.CursorAcp && evidence.Provenance == ModelProvenance.UserDefined
            ? Array.Empty<string>() : evidence.SpeedModes;
        return new(evidence.Flags, reasoning, speed, evidence.ContextLimit);
    }

    // Semantic mappings identify an account row, not a selected native model. They may claim only
    // capabilities shared by every selectable model. Route selectors use the per-model map instead.
    private static SanitizedModelCapabilities CommonCapabilities(IReadOnlyList<SanitizedModelCapabilities> values)
    {
        if (values.Count == 0) return SanitizedModelCapabilities.Unknown;
        var flags = values[0].Flags;
        foreach (var item in values.Skip(1)) flags &= item.Flags;
        return new(flags,
            values[0].ReasoningEfforts.Where(value => values.All(item => item.ReasoningEfforts.Contains(value, StringComparer.Ordinal))).ToArray(),
            values[0].SpeedModes.Where(value => values.All(item => item.SpeedModes.Contains(value, StringComparer.Ordinal))).ToArray(),
            values.All(item => item.ContextLimit == values[0].ContextLimit) ? values[0].ContextLimit : null);
    }

    private string SanitizeIdentifier(string value)
    {
        var redacted = SanitizeText(value);

        return string.IsNullOrWhiteSpace(redacted) ? SensitiveDataFilter.Placeholder : redacted;
    }

    private string SanitizeText(string value)
    {
        var redacted = _sensitiveDataFilter.Redact(value);
        redacted = SecretReferenceRegex().Replace(redacted, SensitiveDataFilter.Placeholder);

        return redacted;
    }

    [GeneratedRegex(@"urn:llmworkgui:secret:[^\s""']*", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferenceRegex();
}
