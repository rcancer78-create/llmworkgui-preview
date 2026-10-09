using System.Text.Json;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>Prevents direct aggregate callers from substituting an unguarded transition definition.</summary>
internal static class WorkflowPinnedStageContract
{
    public static void EnsureMatches(string json, WorkflowStageDefinition stage)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var matches = document.RootElement.GetProperty("stages").EnumerateArray()
                .Where(item => item.GetProperty("stageId").GetString() == stage.StageId).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Pinned stage identity is missing or duplicated.");
            var pinned = matches[0];
            if (pinned.GetProperty("displayName").GetString() != stage.DisplayName
                || pinned.GetProperty("requiredRole").GetString() != stage.RequiredRole
                || pinned.GetProperty("stageKind").GetString() != stage.StageKind.ToString()
                || pinned.GetProperty("requiresUserApproval").GetBoolean() != stage.RequiresUserApproval
                || !pinned.GetProperty("requiredReviewerRoles").EnumerateArray().Select(role => role.GetString())
                    .SequenceEqual(stage.RequiredReviewerRoles, StringComparer.Ordinal)
                || Optional(pinned, "artifactRequirement") != stage.ArtifactRequirement
                || Optional(pinned, "nextStageId") != stage.NextStageId
                || Optional(pinned, "failureStageId") != stage.FailureStageId)
                throw new InvalidOperationException("Transition definition differs from the pinned workflow scheme.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("The transition must use the complete immutable pinned stage definition.", exception);
        }
    }

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
