using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

/// <summary>One selected local Cursor route. Native inventory is not proof of model capabilities.</summary>
public sealed record NativeGatewayRouteActivationPreview(string ObservationId, string RouteId,
    string ProviderProfileId, string AccountId, string ModelId, string ActualEmail,
    string? NativeUserId, string NativeModelId, DateTimeOffset ExpiresAtUtc);

public sealed record NativeGatewayRouteActivationResult(string RouteId, string ActualEmail,
    string NativeModelId, DateTimeOffset ActivatedAtUtc);

public interface INativeGatewayRouteActivationService
{
    Task<NativeGatewayRouteActivationPreview> PreviewAsync(string routeId, string expectedEmail,
        CancellationToken cancellationToken = default);
    Task<NativeGatewayRouteActivationResult> ActivateAsync(string observationId,
        bool confirmUserDeclaredModelSupport, bool allowUnverifiedFirstRequest,
        DataClassification maxDataClass, CancellationToken cancellationToken = default);
}
