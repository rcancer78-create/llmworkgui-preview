using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public sealed class SyntheticRunStep
{
    internal SyntheticRunStep(
        string stageLabel,
        WorkflowRole role,
        Session session,
        Execution execution,
        ObservableRunProjection projection)
    {
        StageLabel = ApplicationGuard.NotBlank(stageLabel, nameof(stageLabel));
        Role = role;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(projection);

        if (!projection.IsSynthetic || projection.EvidenceSource != EvidenceSourceKind.SyntheticFixture)
        {
            throw new ArgumentException(
                "A synthetic run step requires a synthetic projection backed by EvidenceSourceKind.SyntheticFixture.",
                nameof(projection));
        }

        Session = session;
        Execution = execution;
        Projection = projection;
    }

    public string StageLabel { get; }

    public WorkflowRole Role { get; }

    public Session Session { get; }

    public Execution Execution { get; }

    public ObservableRunProjection Projection { get; }

    public bool IsSynthetic => true;

    public bool IsActive => Projection.IsActive;
}
