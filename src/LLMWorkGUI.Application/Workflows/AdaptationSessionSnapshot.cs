using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationSessionSnapshot
{
    public AdaptationSessionSnapshot(
        string sessionId,
        string sourceVersionId,
        int sourceVersionNumber,
        string adapterRouteId,
        string adapterModelId,
        AdaptationGoal goal,
        bool allowExpandedSemanticScope,
        int turnCount,
        IReadOnlyList<AdaptationTurnMessage> turnHistory,
        IReadOnlyList<AdaptationBlockerKind> blockers,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(turnHistory);
        ArgumentNullException.ThrowIfNull(blockers);

        SessionId = ApplicationGuard.NotBlank(sessionId, nameof(sessionId));
        SourceVersionId = ApplicationGuard.NotBlank(sourceVersionId, nameof(sourceVersionId));
        SourceVersionNumber = sourceVersionNumber;
        AdapterRouteId = ApplicationGuard.NotBlank(adapterRouteId, nameof(adapterRouteId));
        AdapterModelId = ApplicationGuard.NotBlank(adapterModelId, nameof(adapterModelId));
        Goal = goal;
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
        TurnCount = turnCount;
        TurnHistory = turnHistory.ToArray();
        Blockers = blockers.ToArray();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SessionId { get; }

    public string SourceVersionId { get; }

    public int SourceVersionNumber { get; }

    public string AdapterRouteId { get; }

    public string AdapterModelId { get; }

    public AdaptationGoal Goal { get; }

    public bool AllowExpandedSemanticScope { get; }

    public int TurnCount { get; }

    public IReadOnlyList<AdaptationTurnMessage> TurnHistory { get; }

    public IReadOnlyList<AdaptationBlockerKind> Blockers { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public bool HasBlockers => Blockers.Count > 0;
}
