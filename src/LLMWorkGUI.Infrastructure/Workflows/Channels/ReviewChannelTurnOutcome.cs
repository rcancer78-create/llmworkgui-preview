namespace LLMWorkGUI.Infrastructure.Workflows.Channels;

/// <summary>
/// What one read-only reviewer turn through the loopback star-cliproxy boundary actually was.
/// <para>
/// These values describe local observations. HTTP failures and cancellation after transport invocation
/// do not prove native non-delivery or termination. Their uncertain execution ownership is retained.
/// Only confirmed local cancellation before transport is safely Cancelled. A completed response still
/// cannot authorize a review verdict without native route identity.
/// </para>
/// <para>
/// No value here is a success. There is deliberately no member that means "the assigned route was
/// observed", because on this host no turn can establish that, and a vocabulary that contained the word
/// would invite a caller to invent one.
/// </para>
/// </summary>
public enum ReviewChannelTurnOutcome
{
    /// <summary>
    /// The requested id is not a persisted <c>Routes</c> row. Nothing was sent: a label is not a route,
    /// and it is never inserted to make the refusal go away.
    /// </summary>
    RouteNotPersisted,

    /// <summary>
    /// The row exists but is missing the account, the provider profile or the model behind it, so the
    /// assigned model cannot even be named. Nothing was sent.
    /// </summary>
    RouteIdentityIncomplete,

    /// <summary>
    /// The turn asked for a write. This channel declares the read-only reviewer capability and nothing
    /// else, so a write turn is refused here rather than narrowed to a read behind the caller's back.
    /// </summary>
    WriteTurnRefused,

    /// <summary>
    /// The template's role binding and the route disagree about the model. Sending the turn would mean
    /// reviewing under a model the pinned assignment did not name, so nothing was sent.
    /// </summary>
    AssignedModelConflict,

    /// <summary>
    /// The turn carried no prompt, so there is nothing to show a model. Nothing was sent: an empty message
    /// is not a shorter review, it is no review at all.
    /// </summary>
    PromptAbsent,

    /// <summary>
    /// No loopback gateway is bound to a review route in this build, so there is nowhere a turn could be
    /// addressed. Nothing was sent.
    /// </summary>
    GatewayNotAddressable,

    /// <summary>
    /// The loopback star-cliproxy client is not composed into this container. Nothing was sent.
    /// </summary>
    GatewayClientNotComposed,

    /// <summary>
    /// The gateway reported a rejection or an error before content. Native delivery remains unconfirmed.
    /// </summary>
    GatewayRefused,

    /// <summary>
    /// Local cancellation. State is Cancelled only before transport; otherwise Ambiguous retains ownership.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Observed evidence contradicted the requested route, or contradicted evidence the same turn had
    /// already reported. Local reading stopped; native termination remains unconfirmed.
    /// </summary>
    RouteSubstitution,

    /// <summary>
    /// The model asked for a tool. A read-only reviewer has no tools to grant, and a turn that wanted one
    /// is not a read-only review of anything.
    /// </summary>
    ToolCallRequested,

    /// <summary>
    /// A stream chunk was not valid JSON. The rest of such a stream cannot be interpreted either, so
    /// reading past it would be reading past the first thing that is known to be wrong.
    /// </summary>
    MalformedStream,

    /// <summary>
    /// The stream ended without a terminal event: a clean EOF is not a completed turn, and a partial
    /// answer is not a review of the whole document.
    /// </summary>
    StreamIncomplete,

    /// <summary>
    /// The stream reported an error after the turn had already begun.
    /// </summary>
    StreamFailed,

    /// <summary>
    /// The turn completed - a <c>[DONE]</c> marker or a finish reason - and the route that served it still
    /// cannot be identified uniquely, because the native provider, account and model the gateway reports do
    /// not reverse-map to a single persisted <c>Routes.Id</c> in this build. The delivery happened and the
    /// answer is real; the route it came from is not, which is why it authorizes nothing.
    /// </summary>
    IdentityNotProvable,

    /// <summary>
    /// The turn faulted in a way this process cannot place. Whether the request reached the model before the
    /// fault is unknown, so this is the one outcome that is deliberately not refined: every uncertain
    /// delivery is reported the same way rather than being split into guesses.
    /// </summary>
    UncertainDelivery,

    /// <summary>Local stored execution/text/data policy was not confirmed; HTTP was not attempted.</summary>
    EgressPolicyRefused,
}
