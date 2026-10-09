using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Security;
using DomainQuota = LLMWorkGUI.Domain.Entities.QuotaSnapshot;
using DomainBucket = LLMWorkGUI.Domain.ValueObjects.QuotaBucket;
using NativeQuota = LLMGateway.Core.QuotaSnapshot;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Maps catalog metadata only. Gateway request aliases are never response-origin evidence.</summary>
public sealed class GatewayCatalogMapper(SensitiveDataFilter filter)
{
    public GatewayCatalogSnapshot Map(
        IReadOnlyList<ProviderInfo> providers, IReadOnlyList<AccountInfo> accounts,
        IReadOnlyList<GatewayModel> models, IReadOnlyList<NativeQuota> quotas, DateTimeOffset now)
    {
        var providerMap = providers.ToDictionary(p => p.Provider);
        var accountMap = accounts.ToDictionary(a => (a.Provider, a.Id));
        foreach (var provider in providers)
            if (!Enum.IsDefined(provider.Provider) || provider.Provider == ProviderKind.Unknown) throw InvalidCatalog();
        foreach (var account in accounts)
        {
            if (!providerMap.ContainsKey(account.Provider)) throw InvalidCatalog();
            ValidateId(account.Id);
        }
        foreach (var model in models)
        {
            if (!accountMap.ContainsKey((model.Provider, model.AccountId))) throw InvalidCatalog();
            ValidateId(model.NativeModel);
        }
        foreach (var quota in quotas)
            if (!accountMap.ContainsKey((quota.Provider, quota.AccountId))) throw InvalidCatalog();

        var profiles = providers.Select(p => new ProviderProfile(
            ProviderId(p.Provider), Label(p.DisplayName), BackendType.NativeGateway, null, null,
            DataClassification.PublicSource, false)).ToArray();
        var mappedAccounts = accounts.Select(a => new Account(
            AccountId(a.Provider, a.Id), ProviderId(a.Provider), Label(a.DisplayName), a.Id,
            AuthState.Unknown, 0, false, HealthState.ProbeRequired, null, null, 1, null)).ToArray();
        var mappedModels = models.GroupBy(m => (m.Provider, m.NativeModel)).Select(group =>
        {
            var m = group.First();
            return new ModelDescriptor(ModelId(m.Provider, m.NativeModel), BackendType.NativeGateway,
                ProviderId(m.Provider), m.NativeModel, Label(m.DisplayName),
                group.Select(x => AccountId(x.Provider, x.AccountId)).Distinct().ToArray(),
                [], [], [], CapabilityState.Unknown, ModelProvenance.PluginReported,
                false, HealthState.ProbeRequired, null, false, false, now);
        }).ToArray();
        var routes = models.DistinctBy(m => (m.Provider, m.AccountId, m.NativeModel)).Select(m =>
            new Route(Key("route", m.Provider, m.AccountId, m.NativeModel),
                new SessionBinding(BackendType.NativeGateway, ProviderId(m.Provider),
                    AccountId(m.Provider, m.AccountId), ModelId(m.Provider, m.NativeModel), null, null, null),
                DataClassification.PublicSource, false, HealthState.ProbeRequired, 0)).ToArray();
        var snapshots = quotas.Select(q => new DomainQuota(
            Key("quota", q.Provider, q.AccountId, q.FetchedAt.ToUniversalTime().ToString("O")),
            AccountId(q.Provider, q.AccountId),
            q.IsStale ? QuotaProvenance.Stale : q.Availability == AccountAvailability.Error
                ? QuotaProvenance.Error : !q.Supported ? QuotaProvenance.Unsupported : QuotaProvenance.Unknown,
            q.FetchedAt, q.Supported ? MapBuckets(q.Buckets) : [], ProviderId(q.Provider),
            expiresAt: q.IsStale ? q.FetchedAt : null)).ToArray();
        return new(profiles, mappedAccounts, mappedModels, routes, snapshots);
    }

    private IReadOnlyList<DomainBucket> MapBuckets(IReadOnlyList<LLMGateway.Core.QuotaBucket> buckets)
    {
        var result = new List<DomainBucket>();
        foreach (var bucket in buckets)
        {
            // A percentage is retained as a percentage, never as an invented absolute quota.
            var unit = bucket.Unit?.ToLowerInvariant() switch
            {
                "requests" => QuotaLimitUnit.Requests, "tokens" => QuotaLimitUnit.Tokens,
                "credits" => QuotaLimitUnit.Credits, "currency" => QuotaLimitUnit.Currency,
                _ => (QuotaLimitUnit?)null
            };
            if (!ValidNumber(bucket.Limit) || !ValidNumber(bucket.Used) || !ValidNumber(bucket.UsedPercent))
                throw InvalidCatalog();
            var percentOnly = bucket.UsedPercent.HasValue && (unit is null || bucket.Limit is null || bucket.Used is null);
            if (percentOnly) unit = QuotaLimitUnit.Percent;
            if (unit is null) continue;
            var window = bucket.WindowMinutes switch
            {
                1 => QuotaLimitWindow.PerMinute, 60 => QuotaLimitWindow.PerHour,
                1440 => QuotaLimitWindow.PerDay, _ => QuotaLimitWindow.Rolling
            };
            result.Add(new DomainBucket(Label(bucket.Name), unit.Value, window, percentOnly ? 100 : bucket.Limit,
                percentOnly ? bucket.UsedPercent : bucket.Used, percentOnly ? bucket.RemainingPercent : bucket.Remaining,
                bucket.ResetsAt, confidence: QuotaConfidence.None));
        }
        return result;
    }

    private static bool ValidNumber(double? value) => value is null || double.IsFinite(value.Value) && value >= 0;
    private void ValidateId(string value)
    {
        if (!BackendModelIdPolicy.TryNormalize(value, out var normalized) || normalized != value
            || filter.Redact(value) != value) throw InvalidCatalog();
    }
    private string Label(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw InvalidCatalog();
        var redacted = filter.Redact(value);
        return redacted.Length > 256 ? redacted[..256] : redacted;
    }
    private static InvalidOperationException InvalidCatalog() => new("Некорректные данные каталога LLMGateway.");
    internal static string ProviderId(ProviderKind provider) => Key("provider", provider);
    internal static string AccountId(ProviderKind provider, string account) => Key("account", provider, account);
    internal static string ModelId(ProviderKind provider, string model) => Key("model", provider, model);
    private static string Key(string kind, ProviderKind provider, params string[] parts) =>
        "llmgateway-" + kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { provider = provider.ToString(), parts })))).ToLowerInvariant();
}
