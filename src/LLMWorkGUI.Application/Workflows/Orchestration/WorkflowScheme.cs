using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// A configurable workflow scheme: the ordered stages, their required roles, reviewer gates, user
/// approvals and transition targets. The standard development chain is a factory, not a hard-coded
/// scenario; callers may declare their own stages as long as the scheme passes validation.
/// </summary>
public sealed class WorkflowScheme
{
    public const string TaskSpecificationStageId = "stage-task-specification";
    public const string ArchitectureStageId = "stage-architecture";
    public const string TechnicalSpecificationStageId = "stage-technical-specification";
    public const string RoadmapStageId = "stage-roadmap";
    public const string DocumentReviewStageId = "stage-document-review";
    public const string UserApprovalStageId = "stage-user-approval";
    public const string ImplementationPackagesStageId = "stage-implementation-packages";
    public const string CodeAndUiStageId = "stage-code-and-ui";
    public const string MultiLevelReviewStageId = "stage-multi-level-review";
    public const string TestsAndUiAcceptanceStageId = "stage-tests-and-ui-acceptance";
    public const string FinalOutcomeStageId = "stage-final-outcome";

    public const string CoordinatorRole = "Coordinator";
    public const string ArchitectRole = "Architect";
    public const string TechnicalWriterRole = "TechnicalWriter";
    public const string ReviewerRole = "Reviewer";
    public const string UiReviewerRole = "UiReviewer";
    public const string ApproverRole = "Approver";
    public const string ImplementerRole = "Implementer";
    public const string TesterRole = "Tester";

    private readonly Dictionary<string, WorkflowStageDefinition> _stagesById;

    public WorkflowScheme(string initialStageId, IReadOnlyList<WorkflowStageDefinition> stages)
    {
        InitialStageId = ApplicationGuard.NotBlank(initialStageId, nameof(initialStageId));
        Stages = CopyStages(stages);

        _stagesById = new Dictionary<string, WorkflowStageDefinition>(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            if (!_stagesById.TryAdd(stage.StageId, stage))
            {
                throw new WorkflowValidationException(
                    $"The stage '{stage.StageId}' is declared more than once.");
            }
        }

        Validate();
    }

    public string InitialStageId { get; }

    public IReadOnlyList<WorkflowStageDefinition> Stages { get; }

    public WorkflowStageDefinition? FindStage(string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);

        return _stagesById.GetValueOrDefault(stageId);
    }

    public WorkflowStageDefinition GetRequiredStage(string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);

        return FindStage(stageId)
            ?? throw new WorkflowValidationException(
                $"The stage '{stageId}' is not declared by the active workflow scheme.");
    }

    public IReadOnlyList<WorkflowStageDefinition> GetTerminalStages() =>
        Stages.Where(stage => stage.NextStageId is null).ToArray();

    /// <summary>
    /// Validates the declared graph: the entry node exists, every next/failure target exists, and a
    /// terminal stage is reachable from the entry node.
    /// </summary>
    public void Validate()
    {
        if (Stages.Count == 0)
        {
            throw new WorkflowValidationException("A workflow scheme must declare at least one stage.");
        }

        if (!_stagesById.ContainsKey(InitialStageId))
        {
            throw new WorkflowValidationException(
                $"The initial stage '{InitialStageId}' is not declared by the scheme.");
        }

        foreach (var stage in Stages)
        {
            if (stage.NextStageId is { } nextStageId && !_stagesById.ContainsKey(nextStageId))
            {
                throw new WorkflowValidationException(
                    $"Stage '{stage.StageId}' declares an unknown next stage '{nextStageId}'.");
            }

            if (stage.FailureStageId is { } failureStageId && !_stagesById.ContainsKey(failureStageId))
            {
                throw new WorkflowValidationException(
                    $"Stage '{stage.StageId}' declares an unknown failure stage '{failureStageId}'.");
            }

            // A reviewer gate or a user approval is a decision about a concrete stored artifact. A stage that
            // declares one without also declaring the artifact it is about would be checked against a hash
            // that no verified document ever produced, so the scheme itself is refused here rather than at
            // the transition boundary, where the reason would arrive far from the declaration.
            if (stage.ArtifactRequirement is null
                && (stage.RequiredReviewerRoles.Count > 0 || stage.RequiresUserApproval))
            {
                throw new WorkflowValidationException(
                    $"Stage '{stage.StageId}' requires "
                    + (stage.RequiresUserApproval
                        ? "a user approval"
                        : $"verdicts from: {string.Join(", ", stage.RequiredReviewerRoles)}")
                    + " but declares no artifact requirement, so the gate would have no stored content to "
                    + "decide on.");
            }
        }

        if (!HasReachableTerminalStage())
        {
            throw new WorkflowValidationException(
                "The workflow scheme cannot reach a terminal stage from its entry node.");
        }
    }

    public static WorkflowScheme CreateStandardDevelopmentScheme()
    {
        return new WorkflowScheme(
            TaskSpecificationStageId,
            new[]
            {
                new WorkflowStageDefinition(
                    TaskSpecificationStageId,
                    "Постановка задачи",
                    CoordinatorRole,
                    WorkflowStageKind.TaskSpecification,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "TaskSpecificationDocument",
                    nextStageId: ArchitectureStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    ArchitectureStageId,
                    "Архитектура",
                    ArchitectRole,
                    WorkflowStageKind.Architecture,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "ArchitectureDocument",
                    nextStageId: TechnicalSpecificationStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    TechnicalSpecificationStageId,
                    "ТЗ",
                    TechnicalWriterRole,
                    WorkflowStageKind.TechnicalSpecification,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "TechnicalSpecificationDocument",
                    nextStageId: RoadmapStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    RoadmapStageId,
                    "Дорожная карта",
                    CoordinatorRole,
                    WorkflowStageKind.Roadmap,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "RoadmapDocument",
                    nextStageId: DocumentReviewStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    DocumentReviewStageId,
                    "Проверка документов",
                    ReviewerRole,
                    WorkflowStageKind.DocumentReview,
                    new[] { ReviewerRole, ArchitectRole },
                    requiresUserApproval: false,
                    artifactRequirement: "DocumentBundle",
                    nextStageId: UserApprovalStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    UserApprovalStageId,
                    "Утверждение",
                    ApproverRole,
                    WorkflowStageKind.UserApproval,
                    Array.Empty<string>(),
                    requiresUserApproval: true,
                    artifactRequirement: "ApprovedDocument",
                    nextStageId: ImplementationPackagesStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    ImplementationPackagesStageId,
                    "Пакеты реализации",
                    CoordinatorRole,
                    WorkflowStageKind.Implementation,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "TaskPacketBundle",
                    nextStageId: CodeAndUiStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    CodeAndUiStageId,
                    "Код/UI",
                    ImplementerRole,
                    WorkflowStageKind.Implementation,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: "ImplementationDiff",
                    nextStageId: MultiLevelReviewStageId,
                    failureStageId: null),
                new WorkflowStageDefinition(
                    MultiLevelReviewStageId,
                    "Многоуровневое ревью",
                    ReviewerRole,
                    WorkflowStageKind.DocumentReview,
                    new[] { ReviewerRole, UiReviewerRole },
                    requiresUserApproval: false,
                    artifactRequirement: "ReviewedImplementationDiff",
                    nextStageId: TestsAndUiAcceptanceStageId,
                    failureStageId: CodeAndUiStageId),
                new WorkflowStageDefinition(
                    TestsAndUiAcceptanceStageId,
                    "Тесты/визуальная приёмка",
                    TesterRole,
                    WorkflowStageKind.UiAcceptance,
                    new[] { TesterRole },
                    requiresUserApproval: true,
                    artifactRequirement: "UiAcceptanceEvidence",
                    nextStageId: FinalOutcomeStageId,
                    failureStageId: CodeAndUiStageId),
                new WorkflowStageDefinition(
                    FinalOutcomeStageId,
                    "Итог",
                    CoordinatorRole,
                    WorkflowStageKind.FinalVerification,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: null,
                    nextStageId: null,
                    failureStageId: null)
            });
    }

    private static IReadOnlyList<WorkflowStageDefinition> CopyStages(
        IReadOnlyList<WorkflowStageDefinition> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var copy = new WorkflowStageDefinition[stages.Count];

        for (var index = 0; index < stages.Count; index++)
        {
            copy[index] = stages[index]
                ?? throw new ArgumentException(
                    "A workflow scheme cannot contain null stages.",
                    nameof(stages));
        }

        return copy;
    }

    private bool HasReachableTerminalStage()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<WorkflowStageDefinition>();

        pending.Enqueue(_stagesById[InitialStageId]);

        while (pending.Count > 0)
        {
            var stage = pending.Dequeue();

            if (!visited.Add(stage.StageId))
            {
                continue;
            }

            if (stage.NextStageId is null)
            {
                return true;
            }

            pending.Enqueue(_stagesById[stage.NextStageId]);

            if (stage.FailureStageId is { } failureStageId)
            {
                pending.Enqueue(_stagesById[failureStageId]);
            }
        }

        return false;
    }
}
