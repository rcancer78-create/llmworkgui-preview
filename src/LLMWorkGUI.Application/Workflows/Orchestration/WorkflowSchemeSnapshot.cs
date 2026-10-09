using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The execution scheme of a run, serialized so that it can be stored on the run itself and rebuilt
/// after a restart without re-reading the template store.
///
/// The snapshot is the only scheme a template-backed run is ever advanced against. It is written once,
/// together with the graph it was derived from, and rebuilt verbatim: no stage, reviewer role, approval
/// requirement, artifact or failure target is inferred at read time, and nothing is filled in from a
/// role name or from the process-wide scheme.
/// </summary>
public sealed class WorkflowSchemeSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ExactStageKindConverter() }
    };

    private readonly string _json;

    public WorkflowSchemeSnapshot(string initialStageId, IReadOnlyList<WorkflowStageDefinition> stages)
    {
        var scheme = new WorkflowScheme(initialStageId, stages);

        InitialStageId = scheme.InitialStageId;
        Scheme = scheme;
        _json = JsonSerializer.Serialize(
            new SnapshotScheme(
                scheme.InitialStageId,
                scheme.Stages
                    .Select(stage => new SnapshotStage(
                        stage.StageId,
                        stage.DisplayName,
                        stage.RequiredRole,
                        stage.StageKind,
                        stage.RequiredReviewerRoles,
                        stage.RequiresUserApproval,
                        stage.ArtifactRequirement,
                        stage.NextStageId,
                        stage.FailureStageId))
                    .ToArray()),
            JsonOptions);
    }

    public string InitialStageId { get; }

    /// <summary>The rebuilt scheme. Every stage, reviewer and approval check of a pinned run uses this.</summary>
    public WorkflowScheme Scheme { get; }

    /// <summary>The stored document, written to the run exactly once and never rewritten.</summary>
    public string Json => _json;

    public static WorkflowSchemeSnapshot CreateFrom(WorkflowScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        return new WorkflowSchemeSnapshot(scheme.InitialStageId, scheme.Stages);
    }

    /// <summary>
    /// Rebuilds a snapshot from a run's stored document. An unreadable or invalid document is refused
    /// instead of being replaced by a default scheme, because a run that cannot state its own scheme
    /// must not advance against somebody else's.
    /// </summary>
    public static WorkflowSchemeSnapshot Deserialize(string json, string runId)
    {
        var guardedJson = ApplicationGuard.NotBlank(json, nameof(json));
        var guardedRunId = ApplicationGuard.NotBlank(runId, nameof(runId));

        SnapshotScheme? document;

        try
        {
            document = JsonSerializer.Deserialize<SnapshotScheme>(guardedJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkflowValidationException(
                $"Workflow run '{guardedRunId}' has an unreadable pinned execution scheme snapshot: "
                    + exception.Message);
        }

        if (document?.Stages is null || document.Stages.Count == 0)
        {
            throw new WorkflowValidationException(
                $"Workflow run '{guardedRunId}' has a pinned execution scheme snapshot without any stage.");
        }

        try
        {
            return new WorkflowSchemeSnapshot(
                document.InitialStageId,
                document.Stages
                    .Select(stage => new WorkflowStageDefinition(
                        stage.StageId,
                        stage.DisplayName,
                        stage.RequiredRole,
                        stage.StageKind,
                        stage.RequiredReviewerRoles ?? throw new WorkflowValidationException("Pinned reviewer roles must be explicit."),
                        stage.RequiresUserApproval,
                        stage.ArtifactRequirement,
                        stage.NextStageId,
                        stage.FailureStageId))
                    .ToArray());
        }
        catch (Exception exception) when (exception is WorkflowValidationException
            or InvalidOperationException
            or ArgumentException
            or FormatException
            or OverflowException)
        {
            throw new WorkflowValidationException(
                $"Workflow run '{guardedRunId}' has a pinned execution scheme snapshot that is not a usable "
                    + $"workflow scheme: {exception.Message}");
        }
    }

    // A pinned snapshot is written by this serializer and rebuilt verbatim. Ordinals and alternative
    // casing are not canonical stage declarations and must not gain meaning through enum coercion.
    private sealed class ExactStageKindConverter : JsonConverter<WorkflowStageKind>
    {
        private static readonly string[] DeclaredNames = Enum.GetNames<WorkflowStageKind>();

        public override WorkflowStageKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("A pinned stage kind must be an exact declared name.");
            var token = reader.GetString();
            if (token is null || !DeclaredNames.Contains(token, StringComparer.Ordinal))
                throw new JsonException("A pinned stage kind must be an exact declared name.");
            return Enum.Parse<WorkflowStageKind>(token);
        }

        public override void Write(Utf8JsonWriter writer, WorkflowStageKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed record SnapshotScheme(
        string InitialStageId,
        IReadOnlyList<SnapshotStage> Stages);

    private sealed record SnapshotStage(
        string StageId,
        string DisplayName,
        string RequiredRole,
        [property: JsonRequired] WorkflowStageKind StageKind,
        [property: JsonRequired] IReadOnlyList<string>? RequiredReviewerRoles,
        [property: JsonRequired] bool RequiresUserApproval,
        string? ArtifactRequirement,
        string? NextStageId,
        string? FailureStageId);
}
