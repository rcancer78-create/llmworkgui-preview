using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Workflows.Channels;

/// <summary>
/// Everything one reviewer turn through the loopback boundary produced, and nothing that would have to be
/// believed.
/// <para>
/// The report is diagnostic evidence about a turn, not a verdict and not an observation. Two fields carry
/// the load. <see cref="ObservedRouteId"/> is always null - a route that was requested is not a route that
/// was used, and this build has no way to turn the native provider, account and model a gateway reports
/// into a unique persisted <c>Routes.Id</c>. <see cref="Outcome"/> therefore never says "succeeded", and
/// the <see cref="ExecutionState"/> next to it is what a run's transition gate reads, which is why an
/// identity that cannot be proven is ambiguous rather than successful.
/// </para>
/// <para>
/// <see cref="Evidence"/> holds only the four identifiers the gateway itself reported, and
/// <see cref="MissingIdentity"/> names which of the three route dimensions it did not. Neither carries
/// model output: content and reasoning events are counted in <see cref="ContentDeltaCount"/>. A complete
/// bounded answer may be carried separately in Response for redacted storage; reasoning is never retained.
/// Response content and gateway metadata establish no native identity or workflow authority.
/// </para>
/// </summary>
public sealed record ReviewChannelTurnReport
{
    /// <summary>Bounded answer from a completed stream; never reasoning or evidence of native identity.</summary>
    public LLMWorkGUI.Application.Workflows.Declarative.WorkflowModelResponse? Response { get; init; }
    private static readonly IReadOnlyList<string> NoIdentity = Array.Empty<string>();

    public ReviewChannelTurnReport(
        string channelId,
        string requestedRouteId,
        ReviewChannelTurnOutcome outcome,
        ExecutionState state,
        string reason,
        StarCliProxyObservedEvidence evidence,
        IReadOnlyList<string>? missingIdentity = null,
        string? nativeSessionId = null,
        int observedEventCount = 0,
        int contentDeltaCount = 0,
        int toolCallCount = 0,
        int malformedEventCount = 0,
        bool terminalEventObserved = false,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        ChannelId = channelId;
        RequestedRouteId = requestedRouteId;
        Outcome = outcome;
        State = state;
        Reason = reason;
        Evidence = evidence;
        MissingIdentity = missingIdentity ?? NoIdentity;
        NativeSessionId = string.IsNullOrWhiteSpace(nativeSessionId) ? null : nativeSessionId;
        ObservedEventCount = observedEventCount;
        ContentDeltaCount = contentDeltaCount;
        ToolCallCount = toolCallCount;
        MalformedEventCount = malformedEventCount;
        TerminalEventObserved = terminalEventObserved;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
    }

    public string ChannelId { get; }

    /// <summary>The route the caller asked for, kept for the audit trail and never used as an answer.</summary>
    public string RequestedRouteId { get; }

    /// <summary>
    /// Always null, and not a constructor parameter: a report is the only place this channel could have
    /// claimed an observed route, so the claim is closed at the type rather than left to each construction
    /// site. There is no overload that can set it, which means a future edit that "just adds" the mapping
    /// cannot leave the existing construction sites looking consistent.
    /// </summary>
    public string? ObservedRouteId => null;

    public ReviewChannelTurnOutcome Outcome { get; }

    /// <summary>
    /// The state a run's transition gate reads. Never <see cref="ExecutionState.Succeeded"/>, because no
    /// turn through this channel can prove the route it used.
    /// </summary>
    public ExecutionState State { get; }

    /// <summary>
    /// One line naming what happened. It never repeats artifact content, a local path, a credential or an
    /// API key, and it never says a turn completed a review.
    /// </summary>
    public string Reason { get; }

    /// <summary>Only the identifiers the gateway reported. Model output is not part of it.</summary>
    public StarCliProxyObservedEvidence Evidence { get; }

    /// <summary>
    /// Which of the three route dimensions - provider, account, model - the gateway did not report, in that
    /// order. A turn that reported all three still has no unique route identity, which is what
    /// <see cref="ReviewChannelTurnOutcome.IdentityNotProvable"/> names.
    /// </summary>
    public IReadOnlyList<string> MissingIdentity { get; }

    /// <summary>
    /// The gateway's own conversation id, when it reported one. It is a native session handle and never a
    /// route: nothing maps it to a <c>Routes.Id</c>, and the review session is written before any turn so
    /// this value is evidence rather than provenance.
    /// </summary>
    public string? NativeSessionId { get; }

    public int ObservedEventCount { get; }

    /// <summary>How many content or reasoning deltas arrived. The text itself is deliberately not kept.</summary>
    public int ContentDeltaCount { get; }

    public int ToolCallCount { get; }

    public int MalformedEventCount { get; }

    /// <summary>
    /// Whether the turn produced a terminal event - a <c>[DONE]</c> marker, a finish reason or an error.
    /// This is the distinction between a completed stream and a clean EOF, and it is recorded rather than
    /// collapsed into the outcome.
    /// </summary>
    public bool TerminalEventObserved { get; }

    public int? PromptTokens { get; }

    public int? CompletionTokens { get; }

    /// <summary>
    /// Whether the gateway reported every dimension that identifies a route. Even when this is true the
    /// observed route stays null: the three values have no unique persisted row behind them yet.
    /// </summary>
    public bool ReportedEveryRouteDimension => MissingIdentity.Count == 0;
}
