using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

/// <summary>Source category of one activity event shown by the Activity Center.</summary>
public enum ActivityEventKind
{
    Execution,
    Session,
    Health,
    UserAction,
    System
}

/// <summary>Normalized state shown by the Activity Center; it is derived from the domain state, never invented.</summary>
public enum ActivityEventState
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Warning
}

/// <summary>Provenance of one activity event. Synthetic fixtures are always marked as such.</summary>
public enum ActivityEventSource
{
    Native,
    Synthetic
}

/// <summary>
/// The five operator-facing roles of the Activity Center filter. They are labels, not a mandatory
/// process: an event keeps the role label the domain reported and falls back to <see cref="System"/>.
/// </summary>
public static class ActivityRoleNames
{
    public const string Architect = "Architect";
    public const string TechLead = "TechLead";
    public const string Reviewer = "Reviewer";
    public const string Coder = "Coder";
    public const string System = "System";

    public static IReadOnlyList<string> All { get; } =
        new[] { Architect, TechLead, Reviewer, Coder, System };

    /// <summary>
    /// Maps an observed or declared role label onto one of the five filter roles. Unknown labels are
    /// never dropped from the stream: they are reported under the System role.
    /// </summary>
    public static string Normalize(string? roleLabel)
    {
        if (string.IsNullOrWhiteSpace(roleLabel))
        {
            return System;
        }

        var candidate = roleLabel.Trim();

        if (Contains(candidate, "architect"))
        {
            return Architect;
        }

        if (Contains(candidate, "techlead") || Contains(candidate, "tech lead") || Contains(candidate, "lead"))
        {
            return TechLead;
        }

        if (Contains(candidate, "review"))
        {
            return Reviewer;
        }

        if (Contains(candidate, "coder")
            || Contains(candidate, "implementer")
            || Contains(candidate, "executor")
            || Contains(candidate, "developer"))
        {
            return Coder;
        }

        return System;
    }

    private static bool Contains(string value, string marker) =>
        value.Contains(marker, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One immutable item of the application activity stream: workflow runs, sessions, health transitions
/// and explicit user actions. All free-text fields are expected to be pre-redacted; the search index
/// additionally runs the configured redactor before anything is indexed (ТЗ §9.3).
/// </summary>
public sealed class ActivityEvent
{
    public ActivityEvent(
        string id,
        DateTimeOffset occurredAtUtc,
        ActivityEventKind kind,
        string role,
        ActivityEventState state,
        ActivityEventSource source,
        string title,
        string description,
        string? sessionId = null,
        string? executionId = null,
        string? routeId = null,
        string? diffText = null,
        string? artifactName = null,
        string? artifactContent = null,
        long? artifactSizeBytes = null,
        string? artifactSha256 = null,
        string? artifactChangeStatus = null)
    {
        Id = ApplicationGuard.NotBlank(id, nameof(id));
        Role = ApplicationGuard.NotBlank(role, nameof(role));
        Title = ApplicationGuard.NotBlank(title, nameof(title));

        if (artifactSizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(artifactSizeBytes),
                artifactSizeBytes,
                "Artifact size must not be negative.");
        }

        OccurredAtUtc = occurredAtUtc;
        Kind = kind;
        State = state;
        Source = source;
        Description = description ?? string.Empty;
        SessionId = ApplicationGuard.OptionalNotBlank(sessionId, nameof(sessionId));
        ExecutionId = ApplicationGuard.OptionalNotBlank(executionId, nameof(executionId));
        RouteId = ApplicationGuard.OptionalNotBlank(routeId, nameof(routeId));
        DiffText = diffText;
        ArtifactName = ApplicationGuard.OptionalNotBlank(artifactName, nameof(artifactName));
        ArtifactContent = artifactContent;
        ArtifactSizeBytes = artifactSizeBytes;
        ArtifactSha256 = ApplicationGuard.OptionalNotBlank(artifactSha256, nameof(artifactSha256));
        ArtifactChangeStatus = ApplicationGuard.OptionalNotBlank(artifactChangeStatus, nameof(artifactChangeStatus));
    }

    public string Id { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public ActivityEventKind Kind { get; }

    /// <summary>Operator-facing role label (Architect, TechLead, Reviewer, Coder or System).</summary>
    public string Role { get; }

    public ActivityEventState State { get; }

    public ActivityEventSource Source { get; }

    public string Title { get; }

    public string Description { get; }

    public string? SessionId { get; }

    public string? ExecutionId { get; }

    public string? RouteId { get; }

    /// <summary>Unified diff carried by the event, if any. Displayed by the Diff Artifact Viewer.</summary>
    public string? DiffText { get; }

    public string? ArtifactName { get; }

    public string? ArtifactContent { get; }

    public long? ArtifactSizeBytes { get; }

    public string? ArtifactSha256 { get; }

    public string? ArtifactChangeStatus { get; }

    public bool IsSynthetic => Source == ActivityEventSource.Synthetic;

    public string SyntheticBadge => IsSynthetic ? "SYNTHETIC" : string.Empty;

    public bool HasDiff => !string.IsNullOrWhiteSpace(DiffText);

    public bool HasArtifact => !string.IsNullOrWhiteSpace(ArtifactContent);

    public bool HasRoute => !string.IsNullOrWhiteSpace(RouteId);

    /// <summary>Creates an event from an observable run projection, mapping the execution state honestly.</summary>
    public static ActivityEvent FromExecutionProjection(ObservableRunProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var role = ResolveProjectionRole(projection);

        return new ActivityEvent(
            $"execution:{projection.ExecutionId}",
            projection.LastActivityAtUtc,
            ActivityEventKind.Execution,
            role,
            MapExecutionState(projection.State),
            projection.IsSynthetic ? ActivityEventSource.Synthetic : ActivityEventSource.Native,
            $"Execution {projection.ExecutionId}",
            $"State {projection.State}; observed route {projection.ObservedRouteIdDisplay}; native session {projection.NativeSessionIdDisplay}.",
            sessionId: projection.SessionId,
            executionId: projection.ExecutionId,
            routeId: projection.ObservedRouteId);
    }

    /// <summary>Creates an event from a workflow run timeline item (transition or execution).</summary>
    public static ActivityEvent FromWorkflowTimelineItem(WorkflowRunTimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var role = ActivityRoleNames.Normalize(item.Role);
        var state = item.ExecutionState is { } executionState
            ? MapExecutionState(executionState)
            : MapTimelineKind(item.Kind);

        return new ActivityEvent(
            $"workflow-timeline:{item.Sequence}:{item.OccurredAtUtc.UtcTicks}:{item.ExecutionId ?? item.StageId ?? item.Kind.ToString()}",
            item.OccurredAtUtc,
            ActivityEventKind.Execution,
            role,
            state,
            item.EvidenceSource == EvidenceSourceKind.SyntheticFixture
                ? ActivityEventSource.Synthetic
                : ActivityEventSource.Native,
            item.Kind == WorkflowRunTimelineItemKind.Transition
                ? $"Stage transition {item.StageDisplay}"
                : $"Execution {item.ExecutionId ?? ObservableRunProjection.NotReportedPlaceholder}",
            item.Description,
            executionId: item.ExecutionId,
            routeId: item.ObservedRouteId);
    }

    /// <summary>Creates an event from a session-scoped activity timeline item.</summary>
    public static ActivityEvent FromActivityTimelineItem(ActivityTimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return FromExecutionProjection(item.Run);
    }

    /// <summary>Health Center transition event (breaker opened, probe executed, recovery verified).</summary>
    public static ActivityEvent FromHealthTransition(
        string scopeId,
        string stateDisplay,
        string detail,
        DateTimeOffset occurredAtUtc)
    {
        ApplicationGuard.NotBlank(scopeId, nameof(scopeId));
        ApplicationGuard.NotBlank(stateDisplay, nameof(stateDisplay));

        return new ActivityEvent(
            $"health:{scopeId}:{occurredAtUtc.UtcTicks}:{stateDisplay}",
            occurredAtUtc,
            ActivityEventKind.Health,
            ActivityRoleNames.System,
            MapHealthState(stateDisplay),
            ActivityEventSource.Native,
            $"Health scope {scopeId}: {stateDisplay}",
            detail ?? string.Empty);
    }

    /// <summary>Explicit user action (filter reset, workflow activation, manual approval...).</summary>
    public static ActivityEvent UserAction(
        string id,
        string title,
        string description,
        DateTimeOffset occurredAtUtc)
    {
        ApplicationGuard.NotBlank(id, nameof(id));

        return new ActivityEvent(
            $"user:{id}",
            occurredAtUtc,
            ActivityEventKind.UserAction,
            ActivityRoleNames.System,
            ActivityEventState.Completed,
            ActivityEventSource.Native,
            title,
            description);
    }

    /// <summary>System event that is not tied to an execution (startup, diagnostics, retention run).</summary>
    public static ActivityEvent SystemEvent(
        string id,
        string title,
        string description,
        DateTimeOffset occurredAtUtc,
        ActivityEventState state = ActivityEventState.Warning)
    {
        ApplicationGuard.NotBlank(id, nameof(id));

        return new ActivityEvent(
            $"system:{id}",
            occurredAtUtc,
            ActivityEventKind.System,
            ActivityRoleNames.System,
            state,
            ActivityEventSource.Native,
            title,
            description);
    }

    /// <summary>Maps a domain execution state onto the five Activity Center states.</summary>
    public static ActivityEventState MapExecutionState(ExecutionState state) => state switch
    {
        ExecutionState.Succeeded => ActivityEventState.Completed,
        ExecutionState.Failed or ExecutionState.TimedOut => ActivityEventState.Failed,
        ExecutionState.Cancelled => ActivityEventState.Cancelled,
        ExecutionState.Ambiguous or ExecutionState.RouteMismatch => ActivityEventState.Warning,
        _ => ActivityEventState.Running
    };

    private static ActivityEventState MapTimelineKind(WorkflowRunTimelineItemKind kind) =>
        kind == WorkflowRunTimelineItemKind.Transition
            ? ActivityEventState.Completed
            : ActivityEventState.Warning;

    /// <summary>
    /// Maps a domain health state onto the five Activity Center states.
    /// <para>
    /// This is the one mapping of a health state onto an Activity Center state, and it is public so the
    /// production producer of health events and the mapping used by the timeline factory cannot drift
    /// apart: a scope that is quarantined on the health screen has to read as failed in the activity
    /// stream too, or the two screens would tell the operator different stories about the same incident.
    /// </para>
    /// </summary>
    public static ActivityEventState MapHealthState(HealthState state) => state switch
    {
        HealthState.QuarantinedAuto or HealthState.DisabledManual => ActivityEventState.Failed,
        HealthState.CoolingDown or HealthState.ProbeRequired or HealthState.Degraded =>
            ActivityEventState.Warning,
        HealthState.Healthy or HealthState.ForcedEnabled or HealthState.Recovering =>
            ActivityEventState.Completed,
        _ => ActivityEventState.Warning
    };

    /// <summary>
    /// Substring mapping for callers that only hold a display string rather than the domain enum, such as
    /// the timeline factory. Prefer the <see cref="HealthState"/> overload where the state is known.
    /// </summary>
    private static ActivityEventState MapHealthState(string stateDisplay)
    {
        if (Enum.TryParse<HealthState>(stateDisplay, ignoreCase: true, out var state)
            && string.Equals(stateDisplay, state.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return MapHealthState(state);
        }

        if (stateDisplay.Contains("Quarantin", StringComparison.OrdinalIgnoreCase)
            || stateDisplay.Contains("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityEventState.Failed;
        }

        if (stateDisplay.Contains("Degrad", StringComparison.OrdinalIgnoreCase)
            || stateDisplay.Contains("Probing", StringComparison.OrdinalIgnoreCase)
            || stateDisplay.Contains("Cooldown", StringComparison.OrdinalIgnoreCase)
            || stateDisplay.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityEventState.Warning;
        }

        return ActivityEventState.Completed;
    }

    private static string ResolveProjectionRole(ObservableRunProjection projection)
    {
        if (projection.Role != WorkflowRole.Unknown)
        {
            return ActivityRoleNames.Normalize(projection.Role.ToString());
        }

        return ActivityRoleNames.Normalize(projection.DisplayLabel);
    }
}
