using System.Globalization;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Presentation item for a single quota bucket row (or an explicit placeholder row when a provider
/// does not report buckets). Keeps the four visual quota classes strictly separated (ТЗ §6.6):
/// trusted numeric (Exact/Plugin), estimated/local, unknown/unsupported, and error/stale.
/// Numeric values and percentages are never fabricated for unverified states.
/// </summary>
public sealed class QuotaBucketItemViewModel
{
    private static readonly Regex SecretSanitizerRegex = new(
        @"(bearer\s+|api[_-]?key[:=]\s*|token[:=]\s*|password[:=]\s*)([^\s;,]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan ScoringTtl = TimeSpan.FromMinutes(5);

    private readonly QuotaSnapshot _snapshot;
    private readonly QuotaBucket? _bucket;

    public QuotaBucketItemViewModel(
        QuotaSnapshot snapshot,
        QuotaBucket? bucket,
        Account? account,
        ProviderProfile? profile,
        AccountQuotaRefreshStatus? refreshStatus,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        _bucket = bucket;

        SnapshotId = snapshot.Id;
        AccountId = snapshot.AccountId;
        AccountName = account?.DisplayName ?? snapshot.AccountId;
        ProviderProfileId = profile?.Id ?? account?.ProviderProfileId ?? snapshot.ProviderProfileId ?? "default";
        ProviderProfileName = profile?.DisplayName ?? ProviderProfileId;
        ModelFamily = snapshot.ModelId ?? "Все модели";
        BucketName = bucket?.BucketName ?? "квота";
        BucketDisplayName = $"{ModelFamily} / {BucketName}";
        Provenance = snapshot.Provenance;

        Remaining = bucket?.RemainingValue;
        Used = bucket?.UsedValue;
        TotalLimit = bucket?.LimitValue;
        Units = FormatUnits(bucket?.Unit);
        ResetAt = bucket?.ResetAt;
        ResetTimeDisplay = ResetAt is { } resetAt ? FormatTimestamp(resetAt) : "Не сообщено";
        LimitWindowDisplay = bucket is null ? "Нет" : FormatWindow(bucket.Window);
        ConfidenceDisplay = bucket is null ? "Нет" : FormatConfidence(bucket.Confidence);

        IsTrustedProvenance = Provenance is QuotaProvenance.ExactProviderReported or QuotaProvenance.PluginReported;
        IsEstimatedLocal = Provenance is QuotaProvenance.LocallyCalculated or QuotaProvenance.Estimated;
        IsUnknownOrUnsupported = Provenance is QuotaProvenance.Unknown or QuotaProvenance.Unsupported;
        IsErrorOrStale = Provenance is QuotaProvenance.Error or QuotaProvenance.Stale;

        var numericCapable = IsTrustedProvenance || IsEstimatedLocal;
        IsNumericQuota = bucket is not null && numericCapable &&
            (bucket.RemainingValue.HasValue || bucket.UsedValue.HasValue || bucket.LimitValue.HasValue);

        CanCalculateScore = snapshot.CanCalculateNumericScore(now, ScoringTtl);

        ProvenanceBadgeText = Provenance switch
        {
            QuotaProvenance.ExactProviderReported => "ТОЧНЫЕ",
            QuotaProvenance.PluginReported => "ДАННЫЕ ПЛАГИНА",
            QuotaProvenance.LocallyCalculated => "ЛОКАЛЬНЫЙ РАСЧЁТ",
            QuotaProvenance.Estimated => "ОЦЕНКА",
            QuotaProvenance.Stale => "УСТАРЕЛИ",
            QuotaProvenance.Unsupported => "НЕ ПОДДЕРЖИВАЕТСЯ",
            QuotaProvenance.Error => "ОШИБКА",
            _ => "НЕИЗВЕСТНО"
        };

        if (IsUnknownOrUnsupported)
        {
            IsFresh = false;
            FreshnessBadgeText = "НИКОГДА";
            LastUpdatedDisplay = "Никогда";
        }
        else if (Provenance == QuotaProvenance.Stale)
        {
            IsFresh = false;
            FreshnessBadgeText = "УСТАРЕЛ";
            LastUpdatedDisplay = FormatTimestamp(snapshot.CapturedAt);
        }
        else if (Provenance == QuotaProvenance.Error)
        {
            IsFresh = false;
            FreshnessBadgeText = "НЕТ ДАННЫХ";
            LastUpdatedDisplay = "Никогда";
        }
        else
        {
            IsFresh = snapshot.IsFresh(now, ScoringTtl);
            FreshnessBadgeText = IsFresh ? "СВЕЖИЙ" : "УСТАРЕЛ";
            LastUpdatedDisplay = FormatTimestamp(snapshot.CapturedAt);
        }

        NextRefresh = refreshStatus?.NextScheduledRefreshAt;
        NextRefreshDisplay = NextRefresh is { } nextRefresh ? FormatTimestamp(nextRefresh) : "Не запланировано";

        ErrorDisplay = BuildErrorDisplay();
        HasError = IsErrorOrStale;
        ScoreEligibilityDisplay = CanCalculateScore
            ? "Доступно числовое оценивание (актуальная доверенная квота)"
            : $"Числовая оценка недоступна ({ProvenanceBadgeText.ToLowerInvariant()})";
    }

    public string SnapshotId { get; }

    public string AccountId { get; }

    public string AccountName { get; }

    public string ProviderProfileId { get; }

    public string ProviderProfileName { get; }

    public string? ModelId => _snapshot.ModelId;

    public string ModelFamily { get; }

    public string BucketName { get; }

    public string BucketDisplayName { get; }

    public QuotaProvenance Provenance { get; }

    public double? Remaining { get; }

    public double? Used { get; }

    public double? TotalLimit { get; }

    public string Units { get; }

    public DateTimeOffset? ResetAt { get; }

    public string ResetTimeDisplay { get; }

    public string LimitWindowDisplay { get; }

    public string ConfidenceDisplay { get; }

    public bool IsTrustedProvenance { get; }

    public bool IsEstimatedLocal { get; }

    public bool IsUnknownOrUnsupported { get; }

    public bool IsErrorOrStale { get; }

    public bool IsNumericQuota { get; }

    public bool CanCalculateScore { get; }

    public bool IsFresh { get; }

    public string ProvenanceBadgeText { get; }

    public string FreshnessBadgeText { get; }

    public string Freshness => FreshnessBadgeText;

    public string LastUpdatedDisplay { get; }

    public DateTimeOffset? NextRefresh { get; }

    public string NextRefreshDisplay { get; }

    public string ErrorDisplay { get; }

    public bool HasError { get; }

    public string ScoreEligibilityDisplay { get; }

    public string RemainingDisplay => IsNumericQuota ? FormatValue(Remaining) : NonNumericPlaceholder;

    public string UsedDisplay => IsNumericQuota ? FormatValue(Used) : NonNumericPlaceholder;

    public string TotalLimitDisplay => IsNumericQuota ? FormatValue(TotalLimit) : NonNumericPlaceholder;

    public string QuotaValueSummary =>
        $"{RemainingDisplay} / {UsedDisplay} / {TotalLimitDisplay} {Units}";

    private string NonNumericPlaceholder => Provenance switch
    {
        QuotaProvenance.Unsupported => "Не поддерживается",
        QuotaProvenance.Stale => "Нет актуальных данных",
        QuotaProvenance.Error => "Нет данных из-за ошибки",
        _ => "Не сообщено"
    };

    private string BuildErrorDisplay()
    {
        if (Provenance == QuotaProvenance.Stale)
        {
            return "Снимок устарел (TTL истёк); числовая оценка отключена (ТЗ §6.6).";
        }

        if (Provenance == QuotaProvenance.Error)
        {
            return SanitizeErrorMessage(_snapshot.ErrorMessage);
        }

        return "Нет";
    }

    private static string FormatValue(double? value) =>
        value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "—";

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatWindow(QuotaLimitWindow value) => value switch
    {
        QuotaLimitWindow.PerMinute => "За минуту",
        QuotaLimitWindow.PerHour => "За час",
        QuotaLimitWindow.PerDay => "За день",
        QuotaLimitWindow.PerMonth => "За месяц",
        QuotaLimitWindow.Rolling => "Скользящее окно",
        QuotaLimitWindow.Total => "За всё время",
        _ => "Неизвестно"
    };

    private static string FormatConfidence(QuotaConfidence value) => value switch
    {
        QuotaConfidence.Exact => "Точная",
        QuotaConfidence.High => "Высокая",
        QuotaConfidence.Medium => "Средняя",
        QuotaConfidence.Low => "Низкая",
        QuotaConfidence.None => "Нет",
        _ => "Неизвестно"
    };

    private static string FormatUnits(QuotaLimitUnit? unit) => unit switch
    {
        QuotaLimitUnit.Requests => "запросов",
        QuotaLimitUnit.Tokens => "токенов",
        QuotaLimitUnit.Credits => "кредитов",
        QuotaLimitUnit.Currency => "денежных единиц",
        QuotaLimitUnit.Percent => "%",
        _ => "единиц"
    };

    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Неизвестная ошибка обновления квоты.";
        }

        var cleaned = SecretSanitizerRegex.Replace(message, "$1***REDACTED***");
        if (cleaned.Length > 300)
        {
            cleaned = cleaned[..300] + "...";
        }

        return cleaned;
    }
}
