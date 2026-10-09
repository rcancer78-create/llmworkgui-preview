using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Strict grammar and fail-closed validation of parameterized model overrides
/// <c>&lt;baseModelId&gt;[&lt;parameter&gt;=&lt;value&gt;,...]</c> (ADR-0003 §7, AC2/AC3). Every
/// lookup uses <see cref="StringComparer.Ordinal"/>; parameters not confirmed by the per-model
/// discovery, parameters whose state is not <see cref="CapabilityState.Supported"/> and values
/// outside the discovered list are rejected before anything reaches the wire.
/// </summary>
public sealed class CursorAcpModelSelector : ICursorAcpModelSelector
{
    public CursorAcpModelSelection Parse(string selection, CursorAcpModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(selection))
        {
            throw Malformed("The model selection must not be empty.");
        }

        if (ContainsWhitespace(selection))
        {
            throw Malformed(
                $"The model selection '{selection}' must not contain whitespace; the grammar is " +
                "<baseModelId>[<parameter>=<value>,...] without spaces.");
        }

        var bracketIndex = selection.IndexOf('[');

        if (bracketIndex < 0)
        {
            if (selection.Contains(']'))
            {
                throw Malformed(
                    $"The model selection '{selection}' contains ']' without an opening '['.");
            }

            return Validate(selection, Array.Empty<CursorAcpOverrideValue>(), catalog);
        }

        if (!selection.EndsWith(']') || selection.IndexOf(']') != selection.Length - 1)
        {
            throw Malformed(
                $"The model selection '{selection}' must end with a single ']' immediately after the overrides.");
        }

        var baseModelId = selection[..bracketIndex];
        var overrideText = selection[(bracketIndex + 1)..^1];

        if (string.IsNullOrEmpty(baseModelId))
        {
            throw Malformed("The model selection must contain a non-empty base model id before '['.");
        }

        if (string.IsNullOrEmpty(overrideText))
        {
            throw Malformed(
                $"The model selection '{selection}' must declare at least one override; " +
                "an empty override set is formatted without brackets.");
        }

        if (overrideText.Contains('[') || overrideText.Contains(']'))
        {
            throw Malformed(
                $"The model selection '{selection}' contains nested or misplaced brackets.");
        }

        var overrides = new List<CursorAcpOverrideValue>();

        foreach (var segment in overrideText.Split(','))
        {
            var equalsIndex = segment.IndexOf('=');

            if (equalsIndex <= 0 ||
                equalsIndex != segment.LastIndexOf('=') ||
                equalsIndex == segment.Length - 1)
            {
                throw Malformed(
                    $"The override segment '{segment}' must have the form <parameter>=<value>.");
            }

            overrides.Add(new CursorAcpOverrideValue
            {
                Name = segment[..equalsIndex],
                Value = segment[(equalsIndex + 1)..]
            });
        }

        return Validate(baseModelId, overrides, catalog);
    }

    public CursorAcpModelSelection Validate(
        string baseModelId,
        IReadOnlyList<CursorAcpOverrideValue> overrides,
        CursorAcpModelCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseModelId);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(catalog);

        var model = catalog.FindModel(baseModelId);

        if (model is null)
        {
            throw new CursorAcpModelSelectionException(
                CursorAcpSelectionFailureKind.UnknownModel,
                $"The base model id '{baseModelId}' is not present in the discovered Cursor ACP model " +
                "catalog; it cannot be validated or sent.");
        }

        var seenParameters = new HashSet<string>(StringComparer.Ordinal);

        foreach (var overrideValue in overrides)
        {
            ArgumentNullException.ThrowIfNull(overrideValue);

            var parameterName = overrideValue.Name;
            var value = overrideValue.Value;

            if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(value))
            {
                throw Malformed("Every override must declare a non-empty parameter name and value.");
            }

            if (!seenParameters.Add(parameterName))
            {
                throw new CursorAcpModelSelectionException(
                    CursorAcpSelectionFailureKind.DuplicateParameter,
                    $"The override parameter '{parameterName}' is declared more than once for '{baseModelId}'.");
            }

            var definition = FindOverrideDefinition(model, parameterName);

            if (definition is null)
            {
                throw new CursorAcpModelSelectionException(
                    CursorAcpSelectionFailureKind.UnknownParameter,
                    $"The override parameter '{parameterName}' is not confirmed by per-model discovery " +
                    $"for '{baseModelId}'; the root parameterStates table is not authoritative (ADR-0003 §7.3).");
            }

            if (definition.CapabilityState != CapabilityState.Supported)
            {
                throw new CursorAcpModelSelectionException(
                    CursorAcpSelectionFailureKind.UnsupportedParameter,
                    $"The override parameter '{parameterName}' of '{baseModelId}' has capability state " +
                    $"'{definition.CapabilityState}' instead of Supported; it cannot be selected or sent.");
            }

            if (!definition.Values.Contains(value, StringComparer.Ordinal))
            {
                throw new CursorAcpModelSelectionException(
                    CursorAcpSelectionFailureKind.InvalidValue,
                    $"The value '{value}' is not a discovered value for parameter '{parameterName}' of " +
                    $"'{baseModelId}'. Allowed values: {string.Join(", ", definition.Values)}.");
            }
        }

        return new CursorAcpModelSelection
        {
            BaseModelId = baseModelId,
            Overrides = overrides.ToArray()
        };
    }

    public string Format(CursorAcpModelSelection selection, CursorAcpModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return Format(selection.BaseModelId, selection.Overrides, catalog);
    }

    public string Format(
        string baseModelId,
        IReadOnlyList<CursorAcpOverrideValue> overrides,
        CursorAcpModelCatalog catalog)
    {
        var validated = Validate(baseModelId, overrides, catalog);

        if (validated.Overrides.Count == 0)
        {
            return validated.BaseModelId;
        }

        var ordered = validated.Overrides
            .OrderBy(overrideValue => overrideValue.Name, StringComparer.Ordinal)
            .Select(overrideValue => $"{overrideValue.Name}={overrideValue.Value}");

        return $"{validated.BaseModelId}[{string.Join(",", ordered)}]";
    }

    private static CursorAcpOverrideDefinition? FindOverrideDefinition(
        CursorAcpModelInfo model,
        string parameterName)
    {
        foreach (var definition in model.Overrides)
        {
            if (string.Equals(definition.Name, parameterName, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static CursorAcpModelSelectionException Malformed(string message) =>
        new(CursorAcpSelectionFailureKind.MalformedSyntax, message);
}
