namespace LLMWorkGUI.Application.Observability;

/// <summary>Exact execution-journal replay identities; ordinary observations never use this operation.</summary>
public static class ActivityJournalReplay
{
    public static bool IsDiagnostic(ActivityEvent item) => item.Id is
        "system:opencode-journal:invalid-events" or "system:native-gateway-journal:invalid-events";

    public static void Validate(ActivityEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var diagnostic = IsDiagnostic(item);
        var lifecycle = (item.Id.StartsWith("opencode-journal:", StringComparison.Ordinal)
                && item.Id.Length > "opencode-journal:".Length)
            || (item.Id.StartsWith("native-gateway-journal:", StringComparison.Ordinal)
                && item.Id.Length > "native-gateway-journal:".Length);
        if (item.Source != ActivityEventSource.Native || item.Role != ActivityRoleNames.System
            || item.DiffText is not null || item.ArtifactContent is not null || item.ArtifactName is not null
            || item.ArtifactSizeBytes is not null || item.ArtifactSha256 is not null || item.ArtifactChangeStatus is not null
            || (diagnostic ? item.Kind != ActivityEventKind.System || item.State != ActivityEventState.Warning
                    || item.SessionId is not null || item.ExecutionId is not null || item.RouteId is not null
                : !lifecycle || item.Kind != ActivityEventKind.Execution || item.SessionId is null || item.ExecutionId is null))
            throw new ArgumentException("Only execution-journal lifecycle evidence and its two diagnostic snapshots can be replayed.", nameof(item));
    }

    public static bool HasSameEvidence(ActivityEvent left, ActivityEvent right) =>
        left.Id == right.Id && left.OccurredAtUtc == right.OccurredAtUtc && left.Kind == right.Kind
        && left.Role == right.Role && left.State == right.State && left.Source == right.Source
        && left.Title == right.Title && left.Description == right.Description
        && left.SessionId == right.SessionId && left.ExecutionId == right.ExecutionId && left.RouteId == right.RouteId
        && left.DiffText == right.DiffText && left.ArtifactName == right.ArtifactName
        && left.ArtifactContent == right.ArtifactContent && left.ArtifactSizeBytes == right.ArtifactSizeBytes
        && left.ArtifactSha256 == right.ArtifactSha256 && left.ArtifactChangeStatus == right.ArtifactChangeStatus;
}
