using LLMWorkGUI.Application.Routing;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Presentation item for a scored routing candidate as reported by the routing engine (ТЗ §4.2, §6.5).
/// Only candidates that received a numeric score from the engine are represented here.
/// </summary>
public sealed class CandidateScoreViewModel
{
    public CandidateScoreViewModel(
        string accountId,
        string accountName,
        CandidateScore score,
        bool isSelected,
        string? quotaSnapshotId)
    {
        ArgumentNullException.ThrowIfNull(score);

        AccountId = accountId;
        AccountName = accountName;
        TotalScore = score.TotalScore;
        QuotaScore = score.QuotaScore;
        HealthScore = score.HealthScore;
        PriorityScore = score.PriorityScore;
        LoadScore = score.LoadScore;
        LatencyScore = score.LatencyScore;
        ReserveScore = score.ReserveScore;
        IsSelected = isSelected;
        QuotaSnapshotId = quotaSnapshotId;
    }

    public string AccountId { get; }

    public string AccountName { get; }

    public double TotalScore { get; }

    public double QuotaScore { get; }

    public double HealthScore { get; }

    public double PriorityScore { get; }

    public double LoadScore { get; }

    public double LatencyScore { get; }

    public double ReserveScore { get; }

    public bool IsSelected { get; }

    public string? QuotaSnapshotId { get; }

    public string SelectionBadgeText => IsSelected ? "ВЫБРАН" : "ОЦЕНЕН";

    public string TotalScoreDisplay => $"Итого {TotalScore:F1} / 100";

    public string ComponentBreakdownDisplay =>
        $"Квота {QuotaScore:F1} · Здоровье {HealthScore:F1} · Приоритет {PriorityScore:F1} · " +
        $"Нагрузка {LoadScore:F1} · Задержка {LatencyScore:F1} · Резерв {ReserveScore:F1}";

    public string QuotaSnapshotDisplay => string.IsNullOrWhiteSpace(QuotaSnapshotId)
        ? "Нет связанного снимка"
        : $"Снимок {QuotaSnapshotId}";
}
