using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

public sealed record DataClassificationGateDecision
{
    public required bool IsAllowed { get; init; }
    public string? Explanation { get; init; }

    public static DataClassificationGateDecision Allowed() => new() { IsAllowed = true };

    public static DataClassificationGateDecision Blocked(string explanation) => new() { IsAllowed = false, Explanation = explanation };
}

public interface IDataClassificationGate
{
    Task<DataClassificationGateDecision> EvaluateAsync(
        DataClassification projectDataClass,
        string? providerProfileId,
        bool isManualOnly = false,
        CancellationToken cancellationToken = default);
}
