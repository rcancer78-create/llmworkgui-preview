namespace LLMWorkGUI.Application.Providers;

public sealed record ConfigPreviewResult(
    string GeneratedJson,
    string RedactedJson,
    string DiffText,
    bool HasChanges,
    bool HasExistingConfig);
