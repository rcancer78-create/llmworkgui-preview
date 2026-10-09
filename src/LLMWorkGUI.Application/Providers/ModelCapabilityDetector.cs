namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Projects an inventory identity and guards explicit capability selections.
/// A model name or owner does not establish any supported capability or context limit.
/// </summary>
public static class ModelCapabilityDetector
{
    public static DiscoveredModelDetails DetectCapabilities(string modelId, string? modelName = null, string? ownedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var displayName = string.IsNullOrWhiteSpace(modelName) ? modelId : modelName.Trim();

        return new DiscoveredModelDetails(
            modelId.Trim(),
            displayName,
            Description: ownedBy != null ? $"Owned by {ownedBy}" : null,
            Capabilities: ModelCapabilityFlags.None);
    }

    /// <summary>
    /// Validates whether a specific model option (e.g. reasoning_effort, vision, tools) is supported
    /// by the model descriptor, throwing an exception if unsupported.
    /// </summary>
    public static void ValidateOptionSupported(DiscoveredModelDetails model, string optionName, object? optionValue = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionName);

        var normOption = optionName.Trim().ToLowerInvariant();

        if (normOption is "reasoning_effort" or "reasoning" or "thinking")
        {
            if (!model.SupportsReasoning || model.SupportedReasoningEfforts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Option '{optionName}' is not supported by model '{model.Id}': no confirmed reasoning values are available.");
            }

            if (optionValue is not string strVal || string.IsNullOrWhiteSpace(strVal)
                || !model.SupportedReasoningEfforts.Contains(strVal, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"Reasoning effort is not valid for model '{model.Id}'. Allowed: {string.Join(", ", model.SupportedReasoningEfforts)}.",
                    nameof(optionValue));
        }
        else if (normOption is "vision" or "image_input")
        {
            if (!model.SupportsVision)
            {
                throw new InvalidOperationException(
                    $"Option '{optionName}' is not supported by model '{model.Id}'. Model does not support vision/image inputs.");
            }
        }
        else if (normOption is "tools" or "tool_calling" or "function_calling")
        {
            if (!model.SupportsToolCalling)
            {
                throw new InvalidOperationException(
                    $"Option '{optionName}' is not supported by model '{model.Id}'. Model does not support tool/function calling.");
            }
        }
        else
        {
            throw new ArgumentException("The model option name is not recognized.", nameof(optionName));
        }
    }

    public static bool IsOptionSupported(DiscoveredModelDetails model, string optionName)
    {
        if (model == null || string.IsNullOrWhiteSpace(optionName)) return false;

        var norm = optionName.Trim().ToLowerInvariant();
        return norm switch
        {
            "reasoning_effort" or "reasoning" or "thinking" => model.SupportsReasoning && model.SupportedReasoningEfforts.Count > 0,
            "vision" or "image_input" => model.SupportsVision,
            "tools" or "tool_calling" or "function_calling" => model.SupportsToolCalling,
            _ => false
        };
    }

}
