using System.Text.Json;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Semantic role/stage/quality/escalation guard of ТЗ §6.14, decided from real evidence.
/// <para>
/// Role mappings are compared against the declared role baseline of the source version. Rebinding an
/// existing role is not a semantic change; role additions, removals and renames, and mappings the model
/// itself marks as semantic changes, are reported as differences.
/// </para>
/// <para>
/// Independently of any model claim, the real source and candidate packages are compared by
/// <see cref="SemanticPackageComparison"/>. A declared-stage, quality-gate, escalation or role-prose
/// change is reported as <see cref="AdaptationBlockerKind.DisallowedSemanticChange"/> even when the model
/// declared <c>isSemanticChange: false</c>, and a semantic-bearing file that cannot be compared
/// confidently is reported as a blocker as well.
/// </para>
/// <para>
/// <b>No consent path exists inside this engine.</b> Every detected difference is a blocker here, so the
/// only way past it is the post-diff, per-issue acknowledgement of the activation decision UI, which
/// revalidates the real source and candidate content before the pointer moves. The pre-send expanded
/// scope flag is a prompt input, never an approval, and a change whose evidence could not be read is never
/// clearable at all.
/// </para>
/// </summary>
public sealed class SemanticDiffEngine : ISemanticDiffEngine
{
    public SemanticDiffResult Compare(
        string? declaredRolesJson,
        IReadOnlyList<SemanticRoleMapping> mappings,
        bool allowExpandedSemanticScope)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        return Compare(new SemanticDiffRequest(declaredRolesJson, mappings, allowExpandedSemanticScope));
    }

    public SemanticDiffResult Compare(SemanticDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<AdaptationValidationIssue>();
        var changes = new List<string>();
        var detected = new List<SemanticChange>();

        CompareRoleMappings(request, issues, changes);
        ComparePackages(request, issues, changes, detected);

        return new SemanticDiffResult(issues, changes, changes.Count > 0, detected);
    }

    private static void CompareRoleMappings(
        SemanticDiffRequest request,
        List<AdaptationValidationIssue> issues,
        List<string> changes)
    {
        foreach (var mapping in request.Mappings)
        {
            if (!mapping.IsSemanticChange)
            {
                continue;
            }

            var message = $"Role '{mapping.Role}' is marked as a semantic change: {mapping.Rationale}";

            changes.Add(message);
            issues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DisallowedSemanticChange,
                mapping.Role.ToString(),
                message));
        }

        if (!TryParseDeclaredRoles(request.DeclaredRolesJson, out var declaredRoles))
        {
            const string message = "The declared role baseline cannot be verified because its JSON or role names are invalid.";
            changes.Add(message);
            issues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DisallowedSemanticChange, role: null, message, isNotClearable: true));
            return;
        }

        if (declaredRoles.Count == 0)
        {
            return;
        }

        var targetRoles = request.Mappings
            .Select(mapping => mapping.Role)
            .Distinct()
            .OrderBy(role => role)
            .ToArray();

        foreach (var addedRole in targetRoles.Where(role => !declaredRoles.Contains(role)))
        {
            var message = $"Role '{addedRole}' is added relative to the declared roles of the source version.";

            changes.Add(message);
            issues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DisallowedSemanticChange,
                addedRole.ToString(),
                message));
        }

        foreach (var removedRole in declaredRoles
                     .Where(role => !targetRoles.Contains(role))
                     .OrderBy(role => role))
        {
            var message = $"Role '{removedRole}' declared by the source version is no longer mapped.";

            changes.Add(message);
            issues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DisallowedSemanticChange,
                removedRole.ToString(),
                message));
        }
    }

    private static void ComparePackages(
        SemanticDiffRequest request,
        List<AdaptationValidationIssue> issues,
        List<string> changes,
        List<SemanticChange> detected)
    {
        if (request.SourcePackage is null || request.CandidatePackage is null)
        {
            return;
        }

        foreach (var change in SemanticPackageComparison.Compare(request.SourcePackage, request.CandidatePackage))
        {
            detected.Add(change);

            var message = $"Semantic change detected in the candidate package ({change.Kind}, {change.Subject}): {change.Detail}";

            changes.Add(message);

            issues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DisallowedSemanticChange,
                role: null,
                message,
                isNotClearable: change.NonClearable));
        }
    }

    private static bool TryParseDeclaredRoles(string? declaredRolesJson, out IReadOnlyList<WorkflowRole> declaredRoles)
    {
        declaredRoles = Array.Empty<WorkflowRole>();
        if (string.IsNullOrWhiteSpace(declaredRolesJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(declaredRolesJson);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var roles = new List<WorkflowRole>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !WorkflowRoleText.TryParse(element.GetString(), out var role))
                {
                    return false;
                }

                if (!roles.Contains(role))
                {
                    roles.Add(role);
                }
            }

            declaredRoles = roles;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
