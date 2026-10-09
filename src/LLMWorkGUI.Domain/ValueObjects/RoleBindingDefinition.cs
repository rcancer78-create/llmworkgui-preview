namespace LLMWorkGUI.Domain.ValueObjects;

public sealed class RoleBindingDefinition
{
    public RoleBindingDefinition(
        string roleId,
        string? primaryRouteId = null,
        IReadOnlyList<string>? fallbackRouteIds = null,
        IReadOnlyList<string>? requiredCapabilities = null,
        string? modelId = null)
    {
        RoleId = DomainGuard.NotBlank(roleId, nameof(roleId));
        PrimaryRouteId = DomainGuard.OptionalNotBlank(primaryRouteId, nameof(primaryRouteId));
        ModelId = DomainGuard.OptionalNotBlank(modelId, nameof(modelId));
        FallbackRouteIds = CopyValues(fallbackRouteIds, nameof(fallbackRouteIds));
        RequiredCapabilities = CopyValues(requiredCapabilities, nameof(requiredCapabilities));
        AllowedRouteIds = BuildAllowedRoutes(PrimaryRouteId, FallbackRouteIds);
    }

    public string RoleId { get; }

    public string? PrimaryRouteId { get; }

    public string? ModelId { get; }

    public IReadOnlyList<string> FallbackRouteIds { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; }

    public IReadOnlyList<string> AllowedRouteIds { get; }

    public bool AllowsRoute(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return false;
        }

        return AllowedRouteIds.Any(route => string.Equals(route, routeId, StringComparison.Ordinal));
    }

    public string ResolveRoute(string? requestedRouteId = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedRouteId))
        {
            return requestedRouteId;
        }

        if (PrimaryRouteId is not null)
        {
            return PrimaryRouteId;
        }

        if (FallbackRouteIds.Count > 0)
        {
            return FallbackRouteIds[0];
        }

        throw new InvalidOperationException(
            $"Role '{RoleId}' declares no primary or fallback route, so no route can be resolved.");
    }

    public RouteChangeDecision EvaluateRouteChange(
        string? boundRouteId,
        string requestedRouteId,
        string? reason)
    {
        var guardedRequestedRoute = DomainGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));

        if (!AllowsRoute(guardedRequestedRoute))
        {
            return RouteChangeDecision.Mismatch(boundRouteId, guardedRequestedRoute, reason);
        }

        if (boundRouteId is not null
            && string.Equals(boundRouteId, guardedRequestedRoute, StringComparison.Ordinal))
        {
            return RouteChangeDecision.Unchanged(boundRouteId, guardedRequestedRoute);
        }

        if (boundRouteId is not null && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Changing the route of a running native session requires an explicit reason.",
                nameof(reason));
        }

        return RouteChangeDecision.Change(boundRouteId, guardedRequestedRoute, reason);
    }

    private static IReadOnlyList<string> CopyValues(
        IReadOnlyList<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var copy = new string[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = DomainGuard.NotBlank(values[index], parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<string> BuildAllowedRoutes(
        string? primaryRouteId,
        IReadOnlyList<string> fallbackRouteIds)
    {
        var routes = new List<string>(fallbackRouteIds.Count + 1);

        if (primaryRouteId is not null)
        {
            routes.Add(primaryRouteId);
        }

        foreach (var route in fallbackRouteIds)
        {
            if (!routes.Contains(route, StringComparer.Ordinal))
            {
                routes.Add(route);
            }
        }

        return routes.AsReadOnly();
    }
}

public sealed class RouteChangeDecision
{
    private RouteChangeDecision(
        string? boundRouteId,
        string requestedRouteId,
        bool isRouteChanged,
        bool requiresNewNativeSession,
        bool isRouteMismatch,
        string? reason)
    {
        BoundRouteId = boundRouteId;
        RequestedRouteId = requestedRouteId;
        IsRouteChanged = isRouteChanged;
        RequiresNewNativeSession = requiresNewNativeSession;
        IsRouteMismatch = isRouteMismatch;
        Reason = reason;
    }

    public string? BoundRouteId { get; }

    public string RequestedRouteId { get; }

    public bool IsRouteChanged { get; }

    public bool RequiresNewNativeSession { get; }

    public bool IsRouteMismatch { get; }

    public string? Reason { get; }

    public bool CarriesNativeSession => !RequiresNewNativeSession && !IsRouteMismatch;

    public static RouteChangeDecision Unchanged(string boundRouteId, string requestedRouteId) =>
        new(
            boundRouteId,
            requestedRouteId,
            isRouteChanged: false,
            requiresNewNativeSession: false,
            isRouteMismatch: false,
            reason: null);

    public static RouteChangeDecision Change(
        string? boundRouteId,
        string requestedRouteId,
        string? reason) =>
        new(
            boundRouteId,
            requestedRouteId,
            isRouteChanged: boundRouteId is not null,
            requiresNewNativeSession: true,
            isRouteMismatch: false,
            reason: reason);

    public static RouteChangeDecision Mismatch(
        string? boundRouteId,
        string requestedRouteId,
        string? reason) =>
        new(
            boundRouteId,
            requestedRouteId,
            isRouteChanged: false,
            requiresNewNativeSession: false,
            isRouteMismatch: true,
            reason: reason);
}
