namespace LLMWorkGUI.Application.Providers;

public sealed class CustomProviderModelSettings
{
    public CustomProviderModelSettings(string modelId, string? displayName = null, IReadOnlyList<string>? variants = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID cannot be empty.", nameof(modelId));
        }

        ModelId = modelId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ModelId : displayName.Trim();
        Variants = variants ?? Array.Empty<string>();
    }

    public string ModelId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<string> Variants { get; }
}
