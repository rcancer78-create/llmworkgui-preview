using System.Text.Json;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The serialized form of a <see cref="WorkflowGraph"/> as a run pins it. A run stores this document
/// verbatim, so the pinned graph can be read back and compared with the assigned template version later
/// without consulting the template store again.
///
/// This is also the single graph document the durable template store writes, because the two must not
/// drift: a gate that is readable on the store path and lost on the pinning path would let a resolver
/// act on a graph it has never actually seen. One node shape, one serializer, one reader, and the
/// <see cref="WorkflowNodeDefinition.GateMetadata"/> of a node survives both.
///
/// Reading is fail-closed about gates. A document whose node omits the gate property, or declares it as
/// JSON null, describes a graph from before gates were preserved and yields a node with no metadata; the
/// consumer decides what that permits. A document whose node <em>declares</em> a gate must declare all
/// four fields of it, and a partial, unknown or malformed one is a named error rather than a node that
/// quietly loses its reviewer, its approval or its artifact requirement.
/// </summary>
public static class WorkflowGraphSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var document = new SnapshotGraph(
            graph.EntryNodeId,
            graph.Nodes
                .Select(node => new SnapshotNode(
                    node.NodeId,
                    node.Kind.ToString(),
                    node.DisplayName,
                    node.RoleBinding,
                    node.RequiredCapabilities,
                    node.PrimaryRouteId,
                    node.FallbackRouteIds,
                    node.Timeout is { } timeout ? timeout.Ticks : null,
                    node.RetryBudget,
                    node.SuccessTargetNodeId,
                    node.FailureTargetNodeId,
                    node.ConditionExpression,
                    node.ArtifactContract,
                    node.PermissionIntent,
                    WriteGate(node.GateMetadata)))
                .ToArray());

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>
    /// Reads a pinned graph snapshot back into a graph. An unreadable or structurally broken document is
    /// reported as an unusable snapshot rather than as a partially built graph.
    /// </summary>
    public static WorkflowGraph Deserialize(string json, string runId)
    {
        var guardedJson = ApplicationGuard.NotBlank(json, nameof(json));
        var guardedRunId = ApplicationGuard.NotBlank(runId, nameof(runId));

        return ReadGraph(
            guardedJson,
            $"Workflow run '{guardedRunId}'",
            "pinned template graph snapshot");
    }

    /// <summary>
    /// Reads the graph document of a stored template version through the same reader a pinned run uses.
    /// The durable store calls this instead of keeping a second node shape, so a stored template and the
    /// snapshot a run pins from it are the same document read by the same rules - including the refusal
    /// to accept a partial gate.
    /// </summary>
    public static WorkflowGraph ReadStoredTemplateGraph(string graphJson, string templateId, int version)
    {
        var guardedJson = ApplicationGuard.NotBlank(graphJson, nameof(graphJson));
        var guardedTemplateId = ApplicationGuard.NotBlank(templateId, nameof(templateId));

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "A stored template version starts at 1.");
        }

        return ReadGraph(
            guardedJson,
            $"The stored graph of template '{guardedTemplateId}' version {version}",
            "workflow graph");
    }

    private static WorkflowGraph ReadGraph(string json, string context, string documentName)
    {
        SnapshotGraph? document;

        try
        {
            using var raw = JsonDocument.Parse(json);
            if (raw.RootElement.ValueKind == JsonValueKind.Object)
            {
                var nodeCollections = 0;
                foreach (var property in raw.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "nodes", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++nodeCollections > 1)
                        throw Invalid($"{context} declares duplicate graph node collections.");
                    if (property.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var node in property.Value.EnumerateArray())
                    {
                        if (node.ValueKind != JsonValueKind.Object) continue;
                        var gateDeclarations = 0;
                        foreach (var field in node.EnumerateObject())
                            if (string.Equals(field.Name, "gateMetadata", StringComparison.OrdinalIgnoreCase)
                                && ++gateDeclarations > 1)
                                throw Invalid($"{context} declares duplicate gate metadata on a graph node.");
                    }
                }
            }
            document = JsonSerializer.Deserialize<SnapshotGraph>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkflowValidationException(
                $"{context} has an unreadable {documentName}: {exception.Message}");
        }

        if (document?.Nodes is null || document.Nodes.Count == 0)
        {
            throw new WorkflowValidationException(
                $"{context} has a {documentName} without any node.");
        }

        // The gates are read before the graph is built so that a node whose gate is present but
        // unusable keeps its own reason instead of being folded into a generic "not a graph" failure.
        var gates = document.Nodes
            .Select(node => node.GateMetadata is null
                ? null
                : ReadGate(context, node.NodeId, node.GateMetadata.Value))
            .ToArray();

        try
        {
            var index = 0;

            return new WorkflowGraph(
                document.EntryNodeId,
                document.Nodes
                    .Select(node => new WorkflowNodeDefinition(
                        node.NodeId,
                        ParseKind(node.Kind),
                        node.DisplayName,
                        node.RoleBinding,
                        node.RequiredCapabilities,
                        node.PrimaryRouteId,
                        node.FallbackRouteIds,
                        node.TimeoutTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
                        node.RetryBudget,
                        node.SuccessTargetNodeId,
                        node.FailureTargetNodeId,
                        node.ConditionExpression,
                        node.ArtifactContract,
                        node.PermissionIntent,
                        gates[index++]))
                    .ToArray());
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException
            or FormatException
            or OverflowException)
        {
            throw new WorkflowValidationException(
                $"{context} has a {documentName} that is not a usable workflow graph: {exception.Message}");
        }
    }

    private static JsonElement? WriteGate(WorkflowNodeGateMetadata? gate) =>
        gate is null
            ? null
            : JsonSerializer.SerializeToElement(
                new SnapshotGate(
                    gate.StageKind.ToString(),
                    gate.RequiredReviewerRoles,
                    gate.RequiresUserApproval,
                    gate.ArtifactRequirement),
                JsonOptions);

    /// <summary>
    /// Reads one node's declared gate. An absent property and an explicit JSON null both mean the
    /// document predates gate preservation and leave the node without metadata; anything else is a
    /// declaration that has to be complete and has to name declared values.
    /// </summary>
    private static WorkflowNodeGateMetadata? ReadGate(
        string context,
        string? nodeId,
        JsonElement element)
    {
        var subject = $"{context}: the gate metadata of node '{nodeId ?? "?"}'";

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw Invalid(
                $"{subject} must be an object declaring the stage kind, the required reviewer roles, the "
                    + "user approval requirement and the artifact requirement, but it is "
                    + $"{Describe(element.ValueKind)}.");
        }

        var declaredFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("stageKind" or "requiredReviewerRoles" or "requiresUserApproval" or "artifactRequirement"))
                throw Invalid($"{subject} declares the unknown field '{property.Name}'.");
            if (!declaredFields.Add(property.Name))
                throw Invalid($"{subject} declares the duplicate field '{property.Name}'.");
        }

        if (!element.TryGetProperty("stageKind", out var stageKindElement))
        {
            throw Invalid($"{subject} does not declare 'stageKind'.");
        }

        if (stageKindElement.ValueKind is not JsonValueKind.String)
        {
            throw Invalid(
                $"{subject} declares 'stageKind' as {Describe(stageKindElement.ValueKind)} instead of a "
                    + "stage kind name.");
        }

        var stageKind = ParseStageKind(stageKindElement.GetString()!, subject);

        if (!element.TryGetProperty("requiredReviewerRoles", out var reviewersElement))
        {
            throw Invalid($"{subject} does not declare 'requiredReviewerRoles'.");
        }

        if (reviewersElement.ValueKind is not JsonValueKind.Array)
        {
            throw Invalid(
                $"{subject} declares 'requiredReviewerRoles' as {Describe(reviewersElement.ValueKind)} "
                    + "instead of a list of roles. An empty list is how a node states that it requires no "
                    + "reviewer.");
        }

        var reviewers = new List<string>();

        foreach (var reviewer in reviewersElement.EnumerateArray())
        {
            if (reviewer.ValueKind is not JsonValueKind.String)
            {
                throw Invalid(
                    $"{subject} declares a required reviewer role as {Describe(reviewer.ValueKind)} "
                        + "instead of a role name.");
            }

            reviewers.Add(reviewer.GetString()!);
        }

        if (!element.TryGetProperty("requiresUserApproval", out var approvalElement))
        {
            throw Invalid($"{subject} does not declare 'requiresUserApproval'.");
        }

        if (approvalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(
                $"{subject} declares 'requiresUserApproval' as {Describe(approvalElement.ValueKind)} "
                    + "instead of true or false. An omitted flag is not a refused approval.");
        }

        if (!element.TryGetProperty("artifactRequirement", out var artifactElement))
        {
            throw Invalid($"{subject} does not declare 'artifactRequirement'.");
        }

        string? artifactRequirement;

        switch (artifactElement.ValueKind)
        {
            case JsonValueKind.Null:
                artifactRequirement = null;
                break;

            case JsonValueKind.String:
                artifactRequirement = artifactElement.GetString();
                break;

            default:
                throw Invalid(
                    $"{subject} declares 'artifactRequirement' as {Describe(artifactElement.ValueKind)} "
                        + "instead of an artifact name or null.");
        }

        try
        {
            return new WorkflowNodeGateMetadata(
                stageKind,
                reviewers,
                approvalElement.GetBoolean(),
                artifactRequirement);
        }
        catch (ArgumentException exception)
        {
            throw Invalid($"{subject} is not usable: {exception.Message}");
        }
    }

    private static WorkflowStageKind ParseStageKind(string token, string subject)
    {
        // The declared names only: a token that is not one of them - including a numeric literal, which
        // would otherwise be accepted as an ordinal - is an unknown stage kind and fails closed.
        foreach (var name in Enum.GetNames<WorkflowStageKind>())
        {
            if (string.Equals(name, token, StringComparison.Ordinal))
            {
                return Enum.Parse<WorkflowStageKind>(name);
            }
        }

        throw Invalid($"{subject} declares the unknown stage kind '{token}'.");
    }

    private static WorkflowValidationException Invalid(string message) =>
        new($"[invalid-node-gate-metadata] {message}");

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "absent",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "an unsupported value"
    };

    private static WorkflowNodeKind ParseKind(string kind) =>
        Enum.GetNames<WorkflowNodeKind>().Contains(kind, StringComparer.Ordinal)
            ? Enum.Parse<WorkflowNodeKind>(kind)
            : throw new FormatException($"'{kind}' is not a known workflow node kind.");

    private sealed record SnapshotGraph(string EntryNodeId, IReadOnlyList<SnapshotNode> Nodes);

    private sealed record SnapshotNode(
        string NodeId,
        string Kind,
        string DisplayName,
        string RoleBinding,
        IReadOnlyList<string> RequiredCapabilities,
        string? PrimaryRouteId,
        IReadOnlyList<string> FallbackRouteIds,
        long? TimeoutTicks,
        int RetryBudget,
        string? SuccessTargetNodeId,
        string? FailureTargetNodeId,
        string? ConditionExpression,
        string? ArtifactContract,
        string? PermissionIntent,
        JsonElement? GateMetadata);

    private sealed record SnapshotGate(
        string StageKind,
        IReadOnlyList<string> RequiredReviewerRoles,
        bool RequiresUserApproval,
        string? ArtifactRequirement);
}
