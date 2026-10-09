using System.Text.Json;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Strict, fail-safe parser for the ТЗ §6.14 adaptation result schema. Malformed JSON, a missing
/// "mappings" array, unparseable required mapping fields, or malformed optional sections never
/// throw: the response is reported as invalid with
/// <see cref="AdaptationBlockerKind.InvalidSchema"/> and no candidate files are recovered.
/// </summary>
public sealed class AdaptationResponseParser : IAdaptationResponseParser
{
    public AdaptationParsedResponse Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return AdaptationParsedResponse.Invalid("Model response is empty.");
        }

        if (!TryExtractJsonObjectText(rawText, out var jsonText))
        {
            return AdaptationParsedResponse.Invalid("Model response does not contain a JSON object.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(jsonText);
        }
        catch (JsonException exception)
        {
            return AdaptationParsedResponse.Invalid($"Model response is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return AdaptationParsedResponse.Invalid("Model response JSON root must be an object.");
            }

            if (!root.TryGetProperty("mappings", out var mappingsElement)
                || mappingsElement.ValueKind != JsonValueKind.Array)
            {
                return AdaptationParsedResponse.Invalid("Model response must contain a 'mappings' array.");
            }

            var mappings = new List<SemanticRoleMapping>();
            var blockerKinds = new List<AdaptationBlockerKind>();
            var index = -1;

            foreach (var mappingElement in mappingsElement.EnumerateArray())
            {
                index++;

                if (mappingElement.ValueKind != JsonValueKind.Object)
                {
                    return AdaptationParsedResponse.Invalid($"Mapping at index {index} must be an object.");
                }

                if (!TryParseMapping(mappingElement, out var mapping, out var error))
                {
                    return AdaptationParsedResponse.Invalid($"Mapping at index {index}: {error}");
                }

                mappings.Add(mapping!);

                if (mapping!.BlockerKind is { } blockerKind)
                {
                    blockerKinds.Add(blockerKind);
                }
            }

            if (!TryReadOptionalString(root, "rationale", out var rationale, out var rationaleError))
            {
                return AdaptationParsedResponse.Invalid(rationaleError);
            }

            if (!TryReadStringArray(root, "warnings", out var warnings, out var warningsError))
            {
                return AdaptationParsedResponse.Invalid(warningsError);
            }

            if (!TryReadStringArray(root, "blockers", out var blockers, out var blockersError))
            {
                return AdaptationParsedResponse.Invalid(blockersError);
            }

            if (!TryReadFileModifications(root, out var fileModifications, out var filesError))
            {
                return AdaptationParsedResponse.Invalid(filesError);
            }

            return new AdaptationParsedResponse(
                isValidJson: true,
                parseError: null,
                mappings,
                rationale,
                warnings,
                blockers,
                blockerKinds,
                fileModifications);
        }
    }

    private static bool TryParseMapping(
        JsonElement element,
        out SemanticRoleMapping? mapping,
        out string error)
    {
        mapping = null;

        if (!TryReadRequiredString(element, "role", out var roleText, out error))
        {
            return false;
        }

        if (!WorkflowRoleText.TryParse(roleText, out var role))
        {
            error = "Property 'role' must be one of Coordinator, Executor, Reviewer or Escalation.";
            return false;
        }

        if (!TryReadRequiredString(element, "originalRoute", out var originalRoute, out error)
            || !TryReadRequiredString(element, "targetRoute", out var targetRoute, out error)
            || !TryReadRequiredString(element, "targetModelId", out var targetModelId, out error)
            || !TryReadRequiredString(element, "rationale", out var rationale, out error))
        {
            return false;
        }

        var isSemanticChange = false;

        if (element.TryGetProperty("isSemanticChange", out var semanticElement)
            && semanticElement.ValueKind != JsonValueKind.Null)
        {
            if (semanticElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "Property 'isSemanticChange' must be a boolean.";
                return false;
            }

            isSemanticChange = semanticElement.GetBoolean();
        }

        AdaptationBlockerKind? blockerKind = null;

        if (element.TryGetProperty("blockerKind", out var blockerElement)
            && blockerElement.ValueKind != JsonValueKind.Null)
        {
            if (blockerElement.ValueKind != JsonValueKind.String
                || !TryParseBlockerKind(blockerElement.GetString(), out var parsedKind))
            {
                error = "Property 'blockerKind' must be one of MissingModel, MissingCapability, "
                    + "DisallowedSemanticChange, DetectedSecret, InvalidSchema, Other or null.";
                return false;
            }

            blockerKind = parsedKind;
        }

        mapping = new SemanticRoleMapping(
            role,
            originalRoute,
            targetRoute,
            targetModelId,
            rationale,
            isSemanticChange,
            blockerKind);

        error = string.Empty;
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement element,
        string propertyName,
        out string value,
        out string error)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"Property '{propertyName}' is required and must not be blank.";
            return false;
        }

        value = property.GetString()!;
        error = string.Empty;
        return true;
    }

    private static bool TryReadOptionalString(
        JsonElement root,
        string propertyName,
        out string value,
        out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"Property '{propertyName}' must be a string.";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement root,
        string propertyName,
        out IReadOnlyList<string> values,
        out string error)
    {
        values = Array.Empty<string>();
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            error = $"Property '{propertyName}' must be an array of strings.";
            return false;
        }

        var result = new List<string>();

        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                error = $"Property '{propertyName}' must be an array of strings.";
                return false;
            }

            result.Add(element.GetString() ?? string.Empty);
        }

        values = result;
        return true;
    }

    private static bool TryReadFileModifications(
        JsonElement root,
        out IReadOnlyDictionary<string, string> fileModifications,
        out string error)
    {
        fileModifications = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        if (!root.TryGetProperty("fileModifications", out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            error = "Property 'fileModifications' must be an object of relative path to text content.";
            return false;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in property.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(file.Name) || file.Value.ValueKind != JsonValueKind.String)
            {
                error = "Property 'fileModifications' must be an object of relative path to text content.";
                return false;
            }

            result[file.Name] = file.Value.GetString() ?? string.Empty;
        }

        fileModifications = result;
        return true;
    }

    private static bool TryParseBlockerKind(string? value, out AdaptationBlockerKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "missingmodel":
                kind = AdaptationBlockerKind.MissingModel;
                return true;
            case "missingcapability":
                kind = AdaptationBlockerKind.MissingCapability;
                return true;
            case "disallowedsemanticchange":
                kind = AdaptationBlockerKind.DisallowedSemanticChange;
                return true;
            case "detectedsecret":
                kind = AdaptationBlockerKind.DetectedSecret;
                return true;
            case "invalidschema":
                kind = AdaptationBlockerKind.InvalidSchema;
                return true;
            case "other":
                kind = AdaptationBlockerKind.Other;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryExtractJsonObjectText(string rawText, out string jsonText)
    {
        var trimmed = rawText.Trim().TrimStart('\uFEFF');
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);

        if (fenceStart < 0)
        {
            jsonText = trimmed;
            return jsonText.Length > 0;
        }

        var contentStart = trimmed.IndexOf('\n', fenceStart);

        if (contentStart < 0)
        {
            jsonText = string.Empty;
            return false;
        }

        var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        if (fenceEnd <= contentStart)
        {
            jsonText = string.Empty;
            return false;
        }

        jsonText = trimmed[(contentStart + 1)..fenceEnd].Trim();
        return jsonText.Length > 0;
    }
}

internal static class WorkflowRoleText
{
    public static bool TryParse(string? value, out WorkflowRole role)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "coordinator":
                role = WorkflowRole.Coordinator;
                return true;
            case "executor":
                role = WorkflowRole.Executor;
                return true;
            case "reviewer":
                role = WorkflowRole.Reviewer;
                return true;
            case "escalation":
                role = WorkflowRole.Escalation;
                return true;
            default:
                role = WorkflowRole.Unknown;
                return false;
        }
    }
}
