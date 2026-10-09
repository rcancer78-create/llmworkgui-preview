using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Providers;

/// <summary>The committed profile and reference are returned without a fallible post-commit lookup.</summary>
public sealed record ProviderConfigurationSaveResult(ProviderProfile Profile, string? ApiKeySecretReference);
