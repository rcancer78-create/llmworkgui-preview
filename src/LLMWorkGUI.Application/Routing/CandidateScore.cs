namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Breakdown of the individual normalized component scores (0..100) for a routing candidate (ТЗ §4.2).
/// </summary>
public sealed record CandidateScore
{
    public double TotalScore { get; init; }
    public double QuotaScore { get; init; }
    public double HealthScore { get; init; }
    public double PriorityScore { get; init; }
    public double LoadScore { get; init; }
    public double LatencyScore { get; init; }
    public double ReserveScore { get; init; }

    public override string ToString() =>
        $"Total: {TotalScore:F1} (Quota: {QuotaScore:F1}, Health: {HealthScore:F1}, Priority: {PriorityScore:F1}, Load: {LoadScore:F1}, Latency: {LatencyScore:F1}, Reserve: {ReserveScore:F1})";
}
