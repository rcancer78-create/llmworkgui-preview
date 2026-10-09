using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// One read of the saved model and route configuration for a single route selection.
/// A model is available only when it is enabled, its capability is <see cref="CapabilityState.Supported"/>,
/// and an enabled route binds it to the account with the same reasoning, speed, and execution mode.
/// Unknown capability is not treated as support.
/// </summary>
public sealed class RoutingModelDecision
{
    private readonly ModelRouteConfiguration _configuration;
    private readonly RouteSelectionRequest _request;
    private readonly DateTimeOffset _now;

    public RoutingModelDecision(ModelRouteConfiguration configuration, RouteSelectionRequest request, DateTimeOffset? now = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _now = now ?? DateTimeOffset.UtcNow;
    }

    public string? Reject(string accountId)
    {
        var identity = _configuration.Models
            .Where(model => model.ProfileId == _request.ProviderProfileId
                && model.Backend == _request.Backend
                && (string.Equals(model.Id, _request.ModelId, StringComparison.Ordinal)
                    || string.Equals(model.NativeModelId, _request.ModelId, StringComparison.Ordinal)))
            .ToArray();

        if (identity.Length == 0)
        {
            return $"Model '{_request.ModelId}' is not configured for this provider and backend";
        }

        var usable = identity
            .Where(model => model.IsEnabled && model.Capability == CapabilityState.Supported)
            .ToArray();

        if (usable.Length == 0)
        {
            return $"Model '{_request.ModelId}' is not a supported model for this provider and backend";
        }

        var usableIds = usable.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        var linked = _configuration.Routes
            .Where(route => route.IsEnabled
                && route.ProfileId == _request.ProviderProfileId
                && route.Backend == _request.Backend
                && route.AccountId == accountId
                && usableIds.Contains(route.ModelId))
            .ToArray();

        if (linked.Length == 0)
        {
            return $"Model '{_request.ModelId}' is not available to this account";
        }

        var matched = linked.Where(route =>
            SameOption(route.ReasoningEffort, _request.ReasoningEffort)
            && SameOption(route.SpeedMode, _request.SpeedMode)
            && SameOption(route.Mode, _request.ExecutionMode)).ToArray();

        if (matched.Length == 0)
            return $"Requested reasoning, speed, or mode is not configured for model '{_request.ModelId}' on this account";

        if (string.IsNullOrWhiteSpace(_request.ReasoningEffort) && string.IsNullOrWhiteSpace(_request.SpeedMode)
            && string.IsNullOrWhiteSpace(_request.ExecutionMode)) return null;

        return matched.Any(route => _configuration.Capabilities.Any(evidence =>
            evidence.ModelId == route.ModelId && evidence.AccountId == accountId
            && evidence.SupportsOptions(_request.Backend, _now,
                _request.ReasoningEffort, _request.SpeedMode, _request.ExecutionMode)))
            ? null : $"Requested reasoning, speed, or mode has no current capability confirmation for model '{_request.ModelId}' on this account";
    }

    private static bool SameOption(string? configured, string? requested)
    {
        var left = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        var right = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        return string.Equals(left, right, StringComparison.Ordinal);
    }
}

/// <summary>
/// Loads the saved model and route rows the generic routing gate consults.
/// Compositions that do not register <see cref="IModelRouteConfigurationService"/> leave the gate absent.
/// </summary>
public sealed class ConfiguredRouteModelEligibility
{
    private readonly IModelRouteConfigurationService _configuration;
    private readonly TimeProvider _clock;

    public ConfiguredRouteModelEligibility(IModelRouteConfigurationService configuration, TimeProvider? clock = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RoutingModelDecision> LoadAsync(
        RouteSelectionRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingModelDecision(configuration, request, _clock.GetUtcNow());
    }
}
