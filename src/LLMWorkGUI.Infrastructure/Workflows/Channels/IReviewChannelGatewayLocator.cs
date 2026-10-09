using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;

namespace LLMWorkGUI.Infrastructure.Workflows.Channels;

/// <summary>
/// Where a read-only reviewer turn for a persisted route would be sent, and why there is nowhere to send
/// it when there is nowhere.
/// <para>
/// The turn needs a loopback address, and the address is not a property of the route row: a
/// <c>Routes</c> row names a provider profile, an account and a model, and none of those three is a URL.
/// Which instance should serve a route is a decision the composition has to state, so it is asked for
/// here rather than guessed from a profile or discovered by scanning ports.
/// </para>
/// <para>
/// "No gateway" is a first-class answer, not a missing implementation. This build binds no gateway to a
/// review route, and the shipped locator says so by name rather than leaving the channel to discover at
/// dispatch time that it has no address.
/// </para>
/// </summary>
public interface IReviewChannelGatewayLocator
{
    /// <summary>
    /// The address for <paramref name="assignment"/>, or a named reason why none exists. Implementations
    /// return a location; they never start, stop or probe anything, and they never log the API key.
    /// </summary>
    ReviewChannelGatewayLocation Locate(WorkflowRouteAssignment assignment);
}

/// <summary>An addressable loopback gateway, or the named reason there is none.</summary>
public sealed record ReviewChannelGatewayLocation
{
    private ReviewChannelGatewayLocation(StarCliProxyEndpoint? endpoint, string reason)
    {
        Endpoint = endpoint;
        Reason = reason;
    }

    public StarCliProxyEndpoint? Endpoint { get; }

    /// <summary>Why no address exists. Never blank, and never contains a credential or a local path.</summary>
    public string Reason { get; }

    public bool IsAddressable => Endpoint is not null;

    public static ReviewChannelGatewayLocation At(StarCliProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return new ReviewChannelGatewayLocation(endpoint, "The composition names a loopback gateway for this route.");
    }

    public static ReviewChannelGatewayLocation None(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ReviewChannelGatewayLocation(null, reason);
    }
}

/// <summary>
/// The shipped locator: no review route has a loopback gateway bound to it in this build.
/// <para>
/// This is a refusal with a reason, not a placeholder. Starting a gateway per review turn belongs to the
/// slice that also owns the native route identity, because a started gateway without an identifiable route
/// produces a real delivery whose provenance cannot be recorded - which is the exact state this whole
/// boundary exists to avoid. Until that slice lands, the honest state of a reviewer turn on this host is
/// that it has nowhere to go.
/// </para>
/// </summary>
public sealed class UnaddressableReviewChannelGatewayLocator : IReviewChannelGatewayLocator
{
    /// <summary>
    /// The one reason this build gives for every route. It names the two things a reviewer turn needs and
    /// neither of which exists here: an address to send to, and a route identity to record what came back.
    /// </summary>
    public const string NoGatewayBoundReason =
        "No loopback star-cliproxy gateway is bound to a review route in this build, and none is started "
            + "for a reviewer turn: a started gateway without a unique persisted Routes.Id behind it would "
            + "produce a real delivery whose provenance cannot be recorded.";

    public ReviewChannelGatewayLocation Locate(WorkflowRouteAssignment assignment) =>
        ReviewChannelGatewayLocation.None(NoGatewayBoundReason);
}
