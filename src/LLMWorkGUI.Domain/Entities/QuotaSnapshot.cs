using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.Entities;

/// <summary>
/// Quota snapshot entity representing observed or calculated quota state at a point in time (ТЗ §6.6, ADR-0004 §5).
/// </summary>
public sealed class QuotaSnapshot
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    public QuotaSnapshot(
        string id,
        string accountId,
        QuotaProvenance provenance,
        DateTimeOffset capturedAt,
        IReadOnlyList<QuotaBucket>? buckets = null,
        string? providerProfileId = null,
        string? modelId = null,
        DateTimeOffset? expiresAt = null,
        string? rawRedactedPayloadJson = null,
        string? errorMessage = null)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        AccountId = DomainGuard.NotBlank(accountId, nameof(accountId));
        Provenance = provenance;
        CapturedAt = capturedAt;
        Buckets = buckets is not null
            ? DomainGuard.NotNullList(buckets, nameof(buckets))
            : Array.Empty<QuotaBucket>();
        ProviderProfileId = DomainGuard.OptionalNotBlank(providerProfileId, nameof(providerProfileId));
        ModelId = DomainGuard.OptionalNotBlank(modelId, nameof(modelId));
        ExpiresAt = expiresAt;
        RawRedactedPayloadJson = rawRedactedPayloadJson;
        ErrorMessage = errorMessage;
    }

    public string Id { get; }

    public string AccountId { get; }

    public string? ProviderProfileId { get; }

    public string? ModelId { get; }

    public QuotaProvenance Provenance { get; }

    public IReadOnlyList<QuotaBucket> Buckets { get; }

    public DateTimeOffset CapturedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public string? RawRedactedPayloadJson { get; }

    public string? ErrorMessage { get; }

    /// <summary>
    /// Returns the primary bucket if any buckets are present.
    /// </summary>
    public QuotaBucket? PrimaryBucket => Buckets.Count > 0 ? Buckets[0] : null;

    /// <summary>
    /// Trusted snapshots come directly from provider or verified plugin (ТЗ §6.6).
    /// </summary>
    public bool IsTrusted =>
        Provenance == QuotaProvenance.ExactProviderReported ||
        Provenance == QuotaProvenance.PluginReported;

    /// <summary>
    /// Determines whether the snapshot is within its TTL.
    /// </summary>
    public bool IsFresh(DateTimeOffset now, TimeSpan? customTtl = null)
    {
        if (ExpiresAt.HasValue)
        {
            return ExpiresAt.Value > now;
        }

        var effectiveTtl = customTtl ?? DefaultTtl;
        return CapturedAt + effectiveTtl > now;
    }

    /// <summary>
    /// Automatic quota scoring requires fresh trusted snapshots (ТЗ §6.6).
    /// Unknown, Unsupported, Stale, and Error NEVER receive a numeric score.
    /// </summary>
    public bool CanCalculateNumericScore(DateTimeOffset now, TimeSpan? customTtl = null)
    {
        if (!IsTrusted) return false;
        if (!IsFresh(now, customTtl)) return false;
        return PrimaryBucket?.RemainingFraction.HasValue == true;
    }

    /// <summary>
    /// Checks whether any bucket has violated its hard reserve threshold.
    /// </summary>
    public bool HasHardReserveViolation => Buckets.Any(b => b.HasHardReserveViolation);

    /// <summary>
    /// Creates an Unknown placeholder snapshot for an account when quota reporting is not available.
    /// </summary>
    public static QuotaSnapshot CreateUnknown(string accountId, string? providerProfileId = null, string? modelId = null) =>
        new(
            $"snap-{Guid.NewGuid():N}",
            accountId,
            QuotaProvenance.Unknown,
            DateTimeOffset.UtcNow,
            Array.Empty<QuotaBucket>(),
            providerProfileId,
            modelId,
            expiresAt: null);

    /// <summary>
    /// Creates an Unsupported placeholder snapshot when the backend/provider does not support a quota API.
    /// </summary>
    public static QuotaSnapshot CreateUnsupported(string accountId, string? providerProfileId = null, string? modelId = null) =>
        new(
            $"snap-{Guid.NewGuid():N}",
            accountId,
            QuotaProvenance.Unsupported,
            DateTimeOffset.UtcNow,
            Array.Empty<QuotaBucket>(),
            providerProfileId,
            modelId,
            expiresAt: null);

    /// <summary>
    /// Creates an Error snapshot when a quota fetch attempt fails.
    /// </summary>
    public static QuotaSnapshot CreateError(string accountId, string errorMessage, string? providerProfileId = null, string? modelId = null) =>
        new(
            $"snap-{Guid.NewGuid():N}",
            accountId,
            QuotaProvenance.Error,
            DateTimeOffset.UtcNow,
            Array.Empty<QuotaBucket>(),
            providerProfileId,
            modelId,
            expiresAt: null,
            errorMessage: errorMessage);
}
