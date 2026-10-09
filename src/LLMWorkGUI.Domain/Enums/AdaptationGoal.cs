namespace LLMWorkGUI.Domain.Enums;

public enum AdaptationGoal
{
    CostSaving = 1,
    Quality = 2,
    Speed = 3,
    Balanced = 4
}

public static class AdaptationGoalExtensions
{
    public static string ToWireName(this AdaptationGoal goal)
    {
        return goal switch
        {
            AdaptationGoal.CostSaving => "CostSaving",
            AdaptationGoal.Quality => "Quality",
            AdaptationGoal.Speed => "Speed",
            AdaptationGoal.Balanced => "Balanced",
            _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown adaptation goal.")
        };
    }

    public static string ToDisplayName(this AdaptationGoal goal)
    {
        return goal switch
        {
            AdaptationGoal.CostSaving => "Экономия токенов",
            AdaptationGoal.Quality => "Качество",
            AdaptationGoal.Speed => "Скорость",
            AdaptationGoal.Balanced => "Баланс",
            _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown adaptation goal.")
        };
    }
}
