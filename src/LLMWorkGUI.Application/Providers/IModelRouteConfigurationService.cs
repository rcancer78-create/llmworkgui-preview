using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

public sealed record ModelProfileOption(string Id, string Name, BackendType Backend, DataClassification MaxDataClass, bool IsEnabled);
public sealed record ModelAccountOption(string Id, string ProfileId, string Name, AuthState AuthState, bool IsEnabled)
{
    public string CapabilityRevision { get; init; } = "";
}
public sealed record ConfiguredModel(string Id, string ProfileId, BackendType Backend, string NativeModelId,
    string Name, CapabilityState Capability, ModelProvenance Provenance, bool IsEnabled)
{
    public string CapabilityRevision { get; init; } = "";
}
public sealed record ConfiguredRoute(string Id, string ProfileId, string AccountId, string ModelId,
    string? Mode, DataClassification MaxDataClass, bool IsEnabled, int Priority,
    string? ReasoningEffort, string? SpeedMode, BackendType Backend);
public sealed record ModelRouteConfiguration(IReadOnlyList<ModelProfileOption> Profiles,
    IReadOnlyList<ModelAccountOption> Accounts, IReadOnlyList<ConfiguredModel> Models, IReadOnlyList<ConfiguredRoute> Routes)
{
    public IReadOnlyList<ModelCapabilityEvidence> Capabilities { get; init; } = Array.Empty<ModelCapabilityEvidence>();
}

public sealed record SaveModelConfiguration(string ProfileId, string NativeModelId, string Name,
    CapabilityState Capability, bool IsEnabled, ConfiguredModel? Expected = null);
public sealed record SaveRouteConfiguration(string ProfileId, string AccountId, string ModelId,
    string? Mode, DataClassification MaxDataClass, bool IsEnabled, int Priority, ConfiguredRoute? Expected = null);

/// <summary>Explicit user configuration. Does not discover credentials, authorize accounts or certify native identity.</summary>
public interface IModelRouteConfigurationService
{
    Task<ModelRouteConfiguration> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> SaveModelAsync(SaveModelConfiguration request, CancellationToken cancellationToken = default);
    Task<string> SaveRouteAsync(SaveRouteConfiguration request, CancellationToken cancellationToken = default);
}
