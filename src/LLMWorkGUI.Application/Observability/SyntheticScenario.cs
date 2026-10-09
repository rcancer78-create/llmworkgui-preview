namespace LLMWorkGUI.Application.Observability;

public sealed class SyntheticScenario
{
    internal SyntheticScenario(string name, IReadOnlyList<SyntheticRunStep> steps)
    {
        Name = ApplicationGuard.NotBlank(name, nameof(name));
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A synthetic scenario must contain at least one run step.", nameof(steps));
        }

        Steps = steps.ToArray();
        Projections = Steps.Select(step => step.Projection).ToArray();
    }

    public string Name { get; }

    public IReadOnlyList<SyntheticRunStep> Steps { get; }

    public IReadOnlyList<ObservableRunProjection> Projections { get; }

    public bool IsSynthetic => true;
}
