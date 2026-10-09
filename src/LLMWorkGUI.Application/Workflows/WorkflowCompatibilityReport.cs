using System.Text.Json;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Reads the adaptation provenance of a saved candidate version from its compatibility report column.
/// <para>
/// Only the field the adaptation service itself writes is honoured: the version the adaptation ran
/// against, which decides whether the candidate has a semantic baseline to be compared with. The model's
/// own output is never a source of consent, and this reader deliberately exposes no approval accessor: an
/// expanded-scope confirmation is a post-diff, per-issue operator decision enforced by the activation
/// gate, so no field of a stored report can suppress a semantic blocker. Missing provenance and an
/// unreadable report are distinct: the latter cannot authorize using the candidate as its own baseline.
/// </para>
/// </summary>
public static class WorkflowCompatibilityReport
{
    private const string SourceVersionIdProperty = "sourceVersionId";

    /// <summary>
    /// The version the adaptation ran against, or null when the version carries no adaptation provenance.
    /// A null value means the candidate is its own semantic baseline and no package comparison applies.
    /// Invalid reports throw instead of being represented as absent; activation uses <see cref="ReadProvenance"/>
    /// to turn that evidence gap into a nonclearable issue.
    /// </summary>
    public static string? ReadSourceVersionId(string? compatibilityReportJson)
    {
        var provenance = ReadProvenance(compatibilityReportJson);
        if (provenance.IsInvalid)
        {
            throw new WorkflowValidationException("The adaptation provenance report is invalid.");
        }

        return provenance.SourceVersionId;
    }

    public static WorkflowAdaptationProvenance ReadProvenance(string? compatibilityReportJson)
    {
        if (string.IsNullOrWhiteSpace(compatibilityReportJson))
        {
            return new(null, false);
        }

        if (!TryReadObject(compatibilityReportJson, out var report))
        {
            return new(null, true);
        }

        // Reject duplicate and differently-cased identity fields, even when one value is valid.
        var fields = report.EnumerateObject()
            .Where(property => string.Equals(property.Name, SourceVersionIdProperty, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fields.Length != 1
            || fields[0].Name != SourceVersionIdProperty
            || fields[0].Value.ValueKind != JsonValueKind.String)
        {
            return new(null, true);
        }

        var sourceVersionId = fields[0].Value.GetString();
        return string.IsNullOrWhiteSpace(sourceVersionId)
            ? new(null, true)
            : new(sourceVersionId, false);
    }

    private static bool TryReadObject(string? json, out JsonElement root)
    {
        root = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A null source is absent only when <see cref="IsInvalid"/> is false.</summary>
public sealed record WorkflowAdaptationProvenance(string? SourceVersionId, bool IsInvalid);
