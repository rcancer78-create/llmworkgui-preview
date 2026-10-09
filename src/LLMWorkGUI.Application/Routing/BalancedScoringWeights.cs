namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Normalization weights for the Balanced routing policy (ТЗ §4.2, §6.5).
/// Formula: 0.30*quota + 0.25*health + 0.15*priority + 0.10*load + 0.10*latency + 0.10*reserve = 1.0.
/// </summary>
public sealed class BalancedScoringWeights
{
    public const string SectionName = "Routing:BalancedWeights";

    public double QuotaWeight { get; set; } = 0.30;
    public double HealthWeight { get; set; } = 0.25;
    public double PriorityWeight { get; set; } = 0.15;
    public double LoadWeight { get; set; } = 0.10;
    public double LatencyWeight { get; set; } = 0.10;
    public double ReserveWeight { get; set; } = 0.10;

    public void Validate()
    {
        if (!double.IsFinite(QuotaWeight) || !double.IsFinite(HealthWeight) ||
            !double.IsFinite(PriorityWeight) || !double.IsFinite(LoadWeight) ||
            !double.IsFinite(LatencyWeight) || !double.IsFinite(ReserveWeight) ||
            QuotaWeight < 0 || HealthWeight < 0 || PriorityWeight < 0 ||
            LoadWeight < 0 || LatencyWeight < 0 || ReserveWeight < 0)
        {
            throw new ArgumentOutOfRangeException("All scoring weights must be finite and non-negative.");
        }

        var sum = QuotaWeight + HealthWeight + PriorityWeight + LoadWeight + LatencyWeight + ReserveWeight;
        if (Math.Abs(sum - 1.0) > 0.001)
        {
            throw new InvalidOperationException($"The sum of Balanced scoring weights must equal 1.0, but was {sum:F4}.");
        }
    }
}
