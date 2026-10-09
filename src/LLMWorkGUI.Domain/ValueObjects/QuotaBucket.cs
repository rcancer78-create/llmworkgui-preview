using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// A normalized quota bucket representing remaining/used capacity for a specific unit and window (ТЗ §6.6).
/// </summary>
public sealed class QuotaBucket
{
    public QuotaBucket(
        string bucketName,
        QuotaLimitUnit unit,
        QuotaLimitWindow window,
        double? limitValue = null,
        double? usedValue = null,
        double? remainingValue = null,
        DateTimeOffset? resetAt = null,
        double? hardReserve = null,
        QuotaConfidence confidence = QuotaConfidence.Exact)
    {
        if (limitValue.HasValue && (!double.IsFinite(limitValue.Value) || limitValue.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(limitValue), "Limit value must be finite and nonnegative.");
        }

        if (usedValue.HasValue && (!double.IsFinite(usedValue.Value) || usedValue.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(usedValue), "Used value must be finite and nonnegative.");
        }

        if (remainingValue.HasValue && (!double.IsFinite(remainingValue.Value) || remainingValue.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(remainingValue), "Remaining value must be finite and nonnegative.");
        }

        if (hardReserve.HasValue && (!double.IsFinite(hardReserve.Value) || hardReserve.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(hardReserve), "Hard reserve threshold must be finite and nonnegative.");
        }

        BucketName = DomainGuard.NotBlank(bucketName, nameof(bucketName));
        Unit = unit;
        Window = window;
        LimitValue = limitValue;
        UsedValue = usedValue;
        RemainingValue = remainingValue;
        ResetAt = resetAt;
        HardReserve = hardReserve;
        Confidence = confidence;
    }

    public string BucketName { get; }

    public QuotaLimitUnit Unit { get; }

    public QuotaLimitWindow Window { get; }

    public double? LimitValue { get; }

    public double? UsedValue { get; }

    public double? RemainingValue { get; }

    public DateTimeOffset? ResetAt { get; }

    public double? HardReserve { get; }

    public QuotaConfidence Confidence { get; }

    /// <summary>
    /// Checks whether the remaining quota is at or below the hard reserve threshold.
    /// </summary>
    public bool HasHardReserveViolation =>
        RemainingValue.HasValue && HardReserve.HasValue && RemainingValue.Value <= HardReserve.Value;

    /// <summary>
    /// Remaining fraction in range 0.0 .. 1.0 if both limit and remaining are known.
    /// </summary>
    public double? RemainingFraction =>
        RemainingValue.HasValue && LimitValue.HasValue && LimitValue.Value > 0
            ? Math.Clamp(RemainingValue.Value / LimitValue.Value, 0.0, 1.0)
            : null;

    /// <summary>
    /// Used fraction in range 0.0 .. 1.0 if both limit and used are known.
    /// </summary>
    public double? UsedFraction =>
        UsedValue.HasValue && LimitValue.HasValue && LimitValue.Value > 0
            ? Math.Clamp(UsedValue.Value / LimitValue.Value, 0.0, 1.0)
            : null;
}
