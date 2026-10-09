using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// The account/profile/backend relation a model lookup is made for. The backend is part of the key,
/// so a model id configured for one backend is never reused for another.
/// </summary>
public sealed record ProviderModelCatalogQuery
{
    public ProviderModelCatalogQuery(string providerProfileId, BackendType backend)
    {
        ProviderProfileId = ApplicationGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        Backend = backend;
    }

    public string ProviderProfileId { get; }

    public BackendType Backend { get; }
}

/// <summary>
/// One locally configured backend model. The id is the value a backend request may name; the display
/// name is what the user reads and never reaches a backend request.
/// </summary>
public sealed record ProviderModelDescriptor
{
    public ProviderModelDescriptor(string modelId, string? displayName = null)
    {
        ModelId = ApplicationGuard.NotBlank(modelId, nameof(modelId));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ModelId : displayName.Trim();
    }

    public string ModelId { get; }

    public string DisplayName { get; }
}
