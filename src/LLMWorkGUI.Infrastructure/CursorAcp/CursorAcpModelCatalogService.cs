using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Strict deserializer of the sanitized Cursor ACP model discovery snapshot
/// (<c>acp-model-catalog.json</c>, ADR-0003 §7.1). It performs no network or ACP RPC discovery:
/// the snapshot is the only discovery source in this baseline (AC1).
/// </summary>
public sealed class CursorAcpModelCatalogService : ICursorAcpModelCatalogService
{
    public CursorAcpModelCatalog Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CursorAcpCatalogFormatException(
                $"The Cursor ACP model catalog is not valid JSON: {exception.Message}",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CursorAcpCatalogFormatException(
                    "The Cursor ACP model catalog root must be a JSON object.");
            }

            return new CursorAcpModelCatalog
            {
                SchemaVersion = ReadRequiredInt32(root, "schemaVersion"),
                Protocol = ReadRequiredString(root, "protocol"),
                AgentVersion = ReadRequiredString(root, "agentVersion"),
                Models = ReadModels(root)
            };
        }
    }

    public CursorAcpModelCatalog LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Deserialize(File.ReadAllText(path));
    }

    private static IReadOnlyList<CursorAcpModelInfo> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            throw new CursorAcpCatalogFormatException(
                "The Cursor ACP model catalog must contain a 'models' array.");
        }

        var models = new List<CursorAcpModelInfo>();
        var modelIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            if (modelElement.ValueKind != JsonValueKind.Object)
            {
                throw new CursorAcpCatalogFormatException(
                    "Every entry of the Cursor ACP model catalog 'models' array must be a JSON object.");
            }

            var modelId = ReadRequiredString(modelElement, "modelId");

            if (!modelIds.Add(modelId))
            {
                throw new CursorAcpCatalogFormatException(
                    $"The Cursor ACP model catalog declares the model id '{modelId}' more than once.");
            }

            models.Add(new CursorAcpModelInfo
            {
                ModelId = modelId,
                Family = ReadRequiredString(modelElement, "family"),
                DisplayName = ReadRequiredString(modelElement, "displayName"),
                DefaultContext = ReadRequiredString(modelElement, "defaultContext"),
                MaxContext = ReadRequiredString(modelElement, "maxContext"),
                Overrides = ReadOverrides(modelElement, modelId)
            });
        }

        return models;
    }

    private static IReadOnlyList<CursorAcpOverrideDefinition> ReadOverrides(
        JsonElement modelElement,
        string modelId)
    {
        if (!modelElement.TryGetProperty("overrides", out var overridesElement) ||
            overridesElement.ValueKind != JsonValueKind.Array)
        {
            throw new CursorAcpCatalogFormatException(
                $"The Cursor ACP model '{modelId}' must declare its per-model 'overrides' array.");
        }

        var overrides = new List<CursorAcpOverrideDefinition>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var overrideElement in overridesElement.EnumerateArray())
        {
            if (overrideElement.ValueKind != JsonValueKind.Object)
            {
                throw new CursorAcpCatalogFormatException(
                    $"Every override of the Cursor ACP model '{modelId}' must be a JSON object.");
            }

            var parameterName = ReadRequiredString(overrideElement, "name");

            if (!parameterNames.Add(parameterName))
            {
                throw new CursorAcpCatalogFormatException(
                    $"The Cursor ACP model '{modelId}' declares the override parameter '{parameterName}' more than once.");
            }

            var values = ReadValues(overrideElement, modelId, parameterName);
            var defaultValue = ReadRequiredString(overrideElement, "default");

            if (!values.Contains(defaultValue, StringComparer.Ordinal))
            {
                throw new CursorAcpCatalogFormatException(
                    $"The Cursor ACP model '{modelId}' declares the default '{defaultValue}' for parameter " +
                    $"'{parameterName}', which is not one of its discovered values ({string.Join(", ", values)}).");
            }

            overrides.Add(new CursorAcpOverrideDefinition
            {
                Name = parameterName,
                Values = values,
                Default = defaultValue,
                CapabilityState = ReadCapabilityState(overrideElement, modelId, parameterName)
            });
        }

        return overrides;
    }

    private static IReadOnlyList<string> ReadValues(
        JsonElement overrideElement,
        string modelId,
        string parameterName)
    {
        if (!overrideElement.TryGetProperty("values", out var valuesElement) ||
            valuesElement.ValueKind != JsonValueKind.Array)
        {
            throw new CursorAcpCatalogFormatException(
                $"The override parameter '{parameterName}' of model '{modelId}' must declare a 'values' array.");
        }

        var values = new List<string>();
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);

        foreach (var valueElement in valuesElement.EnumerateArray())
        {
            if (valueElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(valueElement.GetString()))
            {
                throw new CursorAcpCatalogFormatException(
                    $"The override parameter '{parameterName}' of model '{modelId}' must declare non-empty string values.");
            }

            var value = valueElement.GetString()!;

            if (!uniqueValues.Add(value))
            {
                throw new CursorAcpCatalogFormatException(
                    $"The override parameter '{parameterName}' of model '{modelId}' declares the value '{value}' more than once.");
            }

            values.Add(value);
        }

        if (values.Count == 0)
        {
            throw new CursorAcpCatalogFormatException(
                $"The override parameter '{parameterName}' of model '{modelId}' must declare at least one value.");
        }

        return values;
    }

    private static CapabilityState ReadCapabilityState(
        JsonElement overrideElement,
        string modelId,
        string parameterName)
    {
        var state = ReadRequiredString(overrideElement, "state");

        return state switch
        {
            "Supported" => CapabilityState.Supported,
            "Unsupported" => CapabilityState.Unsupported,
            "Unknown" => CapabilityState.Unknown,
            "Stale" => CapabilityState.Stale,
            "Error" => CapabilityState.Error,
            _ => throw new CursorAcpCatalogFormatException(
                $"The override parameter '{parameterName}' of model '{modelId}' declares the unrecognized " +
                $"state '{state}'. Allowed states: Supported, Unsupported, Unknown, Stale, Error.")
        };
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new CursorAcpCatalogFormatException(
                $"The Cursor ACP model catalog requires a non-empty string '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new CursorAcpCatalogFormatException(
                $"The Cursor ACP model catalog requires a numeric '{propertyName}'.");
        }

        return number;
    }
}
