using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Workflows.Channels;

/// <summary>
/// The production read-only reviewer channel, built on the existing loopback star-cliproxy client.
/// <para>
/// It exists because the assigned-model review needs a real dispatch boundary that is honest about what it
/// can prove, and because the only honest answer this host can give today is a refusal. So the channel
/// declares the reviewer capability, refuses every route, and when it is asked to run a turn anyway it runs
/// a real read-only turn through the loopback client and reports precisely what came back - never a
/// success, and never a route.
/// </para>
/// <para><b>Why <see cref="SupportsRoute"/> is false for every route.</b>
/// Schema 012 provides gateway-native provider/account/model fields and a route key; the strict route
/// resolver can reject missing or ambiguous mappings. Those storage facts are necessary, but they do not
/// establish who answered a particular turn. This production channel still lacks a trusted per-turn
/// response-origin identity and confirmed terminal outcome bound to one persisted route. Request aliases,
/// configured paths/profile labels and gateway-selected metadata are not that evidence.
/// </para>
/// <para>
/// Enabling dispatch requires a proven native protocol carrying the provider, stable account, actual
/// model and route mode or key, plus integration that resolves and persists its observation for the same
/// execution. Until then, neither complete storage nor a successful transport permits a reviewer verdict.
/// </para>
/// <para><b>What a turn can and cannot do here.</b>
///
/// <see cref="ExecuteTurnAsync"/> is implemented for real: it resolves the route, resolves the gateway,
/// sends a read-only request and classifies the outcome. Every classification is bounded, and none of them
/// is a success. The classification deliberately separates a non-2xx status, a completed stream, a clean
/// EOF, a reported error, a cancellation and an uncertain delivery, because those are six different facts
/// and a caller that saw one of them reported as another could read a delivery as a non-delivery or the
/// reverse. After transport invocation, HTTP failure or cancellation cannot prove native termination;
/// execution ownership remains uncertain and blocks another delivery of the same review.
/// </para>
/// <para>
/// Nothing here logs artifact content, model output, a credential or a local path. A bounded answer can
/// be retained after a normal terminal stream; reasoning is excluded. Storage redacts content and checks
/// its hash. A retained answer does not establish native response origin or authorize a verdict.
/// </para>
/// </summary>
public sealed class StarCliProxyReviewReadOnlyChannel : IWorkflowNodeChannel, IWorkflowChannelRouteRefusal
{
    /// <summary>The channel id a catalog, a log and a refusal name.</summary>
    public const string ChannelIdValue = "star-cliproxy-review-readonly";

    /// <summary>The remaining production proof boundary, independent of a route's configured metadata.</summary>
    public const string MissingNativeRouteIdentityReason =
        "native identity storage and a strict route resolver exist, but this production channel has no "
            + "trusted per-turn response-origin proof bound to a confirmed terminal outcome: the "
            + "provider, stable account, actual model and route mode or key must be observed from native "
            + "execution and resolved to one persisted Routes.Id; configured aliases, paths, profile labels "
            + "and gateway-selected metadata do not establish that proof";

    private const string NoRetryNotice = " The turn is not retried.";
    private const string MissingErrorDetails = "<missing-error-details>";

    private readonly IRouteRepository _routeRepository;
    private readonly IReviewChannelGatewayLocator _gatewayLocator;
    private readonly IStarCliProxyClient? _client;
    private readonly LLMWorkGUI.Application.Security.IWorkflowReviewEgressPolicy? _egressPolicy;
    private readonly ILogger<StarCliProxyReviewReadOnlyChannel> _logger;

    public StarCliProxyReviewReadOnlyChannel(
        IRouteRepository routeRepository,
        IReviewChannelGatewayLocator gatewayLocator,
        IStarCliProxyClient? client = null,
        ILogger<StarCliProxyReviewReadOnlyChannel>? logger = null,
        LLMWorkGUI.Application.Security.IWorkflowReviewEgressPolicy? egressPolicy = null)
    {
        _routeRepository = routeRepository ?? throw new ArgumentNullException(nameof(routeRepository));
        _gatewayLocator = gatewayLocator ?? throw new ArgumentNullException(nameof(gatewayLocator));
        _client = client;
        _egressPolicy = egressPolicy;
        _logger = logger ?? NullLogger<StarCliProxyReviewReadOnlyChannel>.Instance;
    }

    public string ChannelId => ChannelIdValue;

    /// <summary>
    /// The one capability this channel declares. It is the read-only reviewer capability and nothing else,
    /// so a catalog asked for anything wider refuses rather than quietly narrowing the turn.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; } =
        new[] { WorkflowReviewRequestService.ReviewerCapability };

    /// <summary>
    /// False for every route, including routes that exist, are enabled and resolve every identity.
    /// <para>
    /// The route row proving a route exists says nothing about which route a gateway served, and this
    /// channel's whole reason for existing is that it will not let the first be read as the second. A true
    /// answer here would hand the catalog a dispatch boundary whose observed route is a guess, and every
    /// gate downstream of it would be reading that guess.
    /// </para>
    /// </summary>
    public bool SupportsRoute(string routeId) => false;

    /// <inheritdoc />
    public string DescribeRouteRefusal(string routeId)
    {
        var guardedRouteId = string.IsNullOrWhiteSpace(routeId) ? "<none>" : routeId;

        return $"Channel '{ChannelIdValue}' supports no route on this host: {MissingNativeRouteIdentityReason}. "
            + $"Route '{guardedRouteId}' was not dispatched, nothing was recorded against it, and no reviewer "
            + "verdict can be produced from it.";
    }

    /// <summary>
    /// Runs one read-only turn and reports it. The returned result never carries an observed route and
    /// never carries <see cref="ExecutionState.Succeeded"/>.
    /// </summary>
    public async Task<WorkflowChannelTurnResult> ExecuteTurnAsync(
        WorkflowChannelTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var report = await RunAsync(request, cancellationToken).ConfigureAwait(false);

        return new WorkflowChannelTurnResult(
            report.State,
            observedRouteId: null,
            report.NativeSessionId,
            report.Reason,
            report.Response);
    }

    /// <summary>
    /// The same turn, with the full bounded classification. It is public so a caller that needs to know
    /// <em>which</em> of the six terminal facts occurred can be told, instead of parsing a state that
    /// deliberately collapses the distinction.
    /// </summary>
    public async Task<ReviewChannelTurnReport> RunAsync(
        WorkflowChannelTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedRouteId = request.RequestedRouteId;

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(requestedRouteId, "The caller cancelled the turn before it was sent.");
        }

        if (!request.IsReadOnly)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.WriteTurnRefused,
                ExecutionState.Failed,
                $"Channel '{ChannelIdValue}' declares the read-only reviewer capability only, so the write turn "
                    + "requested for route '" + requestedRouteId + "' was refused before any request was built.");
        }

        // The route has to be a real row before anything is sent, and the same fail-closed shape the review
        // service uses applies here: a label is not a route and a row with a missing identity cannot name a
        // model at all.
        var assignment = await _routeRepository
            .GetAssignmentAsync(requestedRouteId, cancellationToken)
            .ConfigureAwait(false);

        if (assignment is null)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.RouteNotPersisted,
                ExecutionState.Failed,
                $"Route '{requestedRouteId}' is not a persisted Routes row, so no model is attached to it and "
                    + "no request was sent.");
        }

        if (!assignment.HasEveryIdentity)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.RouteIdentityIncomplete,
                ExecutionState.Failed,
                $"Route '{requestedRouteId}' is missing the required "
                    + $"{string.Join(", ", assignment.MissingIdentities)} identity, so the assigned model "
                    + "cannot be identified and no request was sent.");
        }

        var modelId = ResolveAssignedModel(request, requestedRouteId, assignment, out var modelConflict);

        if (modelConflict is not null)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.AssignedModelConflict,
                ExecutionState.Failed,
                modelConflict);
        }

        if (request.PromptOrCommand is not { } prompt)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.PromptAbsent,
                ExecutionState.Failed,
                $"The read-only reviewer turn for route '{requestedRouteId}' carries no prompt, so there is "
                    + "nothing to show a model and no request was sent.");
        }

        var location = _gatewayLocator.Locate(assignment);

        if (!location.IsAddressable)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.GatewayNotAddressable,
                ExecutionState.Failed,
                $"No loopback gateway addresses route '{requestedRouteId}': {location.Reason} Nothing was sent.");
        }

        if (_client is null)
        {
            return Refused(
                requestedRouteId,
                ReviewChannelTurnOutcome.GatewayClientNotComposed,
                ExecutionState.Failed,
                "The loopback star-cliproxy client is not composed into this container, so no request was sent.");
        }

        // The requested native provider and account are deliberately left unset. This build does not know
        // what the gateway calls them, and asserting our own ProviderProfiles.Id and Accounts.Id as if it
        // did would be a claim the gateway is guaranteed to contradict - which would turn a missing identity
        // into a fabricated route substitution. An absent requested value is the honest request: the client
        // then reports whatever it observed and this channel judges it.
        var context = new WorkflowReviewEgressContext(request.ExecutionId, requestedRouteId, request.Node.NodeId, request.RoleBinding.RoleId);
        if (_egressPolicy is null) return PolicyRefused(requestedRouteId);
        try
        {
            modelId = await _egressPolicy.ValidateAsync(context, location.Endpoint!.BaseUrl.AbsoluteUri,
                prompt, assignment.Route.Binding.ReasoningEffort, request.NativeSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Cancelled(requestedRouteId, "The caller cancelled before HTTP authorization."); }
        catch (Exception) { return PolicyRefused(requestedRouteId); }
        var chatRequest = StarCliProxyChatRequest.Create(
            modelId,
            new[] { new StarCliProxyChatMessage("user", prompt) },
            requestedProviderId: null,
            requestedAccountId: null,
            requestedSessionId: request.NativeSessionId,
            reasoningEffort: assignment.Route.Binding.ReasoningEffort) with { WorkflowReviewContext = context };

        return await StreamAsync(requestedRouteId, location.Endpoint!, chatRequest, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The model the turn must name, or the reason it cannot name one. The pinned role binding is
    /// authoritative - it is what the run's own template assigned to the role - and the route's own model
    /// has to agree with it. A disagreement is a conflict rather than a preference, because either choice
    /// would review under a model the other side did not name.
    /// </summary>
    private static string ResolveAssignedModel(
        WorkflowChannelTurnRequest request,
        string requestedRouteId,
        WorkflowRouteAssignment assignment,
        out string? conflict)
    {
        conflict = null;

        var roleModel = request.RoleBinding.ModelId;
        var routeModel = assignment.Route.Binding.ModelId;

        if (roleModel is not null
            && routeModel is not null
            && !string.Equals(roleModel, routeModel, StringComparison.Ordinal))
        {
            conflict = $"The pinned role binding of role '{request.RoleBinding.RoleId}' names model '{roleModel}' "
                + $"while route '{requestedRouteId}' names model '{routeModel}', so no request was sent.";

            return string.Empty;
        }

        var model = roleModel ?? routeModel;

        if (string.IsNullOrWhiteSpace(model))
        {
            conflict = $"Neither the pinned role binding of role '{request.RoleBinding.RoleId}' nor route "
                + $"'{requestedRouteId}' names a model, so no request was sent.";

            return string.Empty;
        }

        return model;
    }

    private async Task<ReviewChannelTurnReport> StreamAsync(
        string requestedRouteId,
        StarCliProxyEndpoint endpoint,
        StarCliProxyChatRequest chatRequest,
        CancellationToken cancellationToken)
    {
        var evidence = StarCliProxyObservedEvidence.Empty;
        var observedEvents = 0;
        var contentDeltas = 0;
        var toolCalls = 0;
        var malformed = 0;
        int? promptTokens = null;
        int? completionTokens = null;
        var terminalEventObserved = false;
        string? terminalError = null;
        var answer = new StringBuilder();
        var invalidAnswer = false;
        var stoppedNormally = false;

        try
        {
            await foreach (var streamEvent in _client!
                .StreamChatCompletionAsync(endpoint, chatRequest, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                observedEvents++;
                evidence = evidence.Merge(streamEvent.Evidence);

                switch (streamEvent.Kind)
                {
                    case StarCliProxyStreamEventKind.ContentDelta:
                        contentDeltas++;
                        if (terminalEventObserved || streamEvent.Content is null
                            || streamEvent.Content.Length > WorkflowModelResponse.MaximumUtf8Bytes - answer.Length)
                            invalidAnswer = true;
                        if (!invalidAnswer) answer.Append(streamEvent.Content);
                        break;
                    case StarCliProxyStreamEventKind.ReasoningDelta:
                        if (terminalEventObserved) invalidAnswer = true;
                        contentDeltas++;
                        break;

                    case StarCliProxyStreamEventKind.ToolCall:
                        // A read-only reviewer has no tools to grant, so a tool call ends the turn here
                        // rather than being read as review text.
                        toolCalls++;
                        terminalEventObserved = true;

                        return Terminal(
                            requestedRouteId,
                            ReviewChannelTurnOutcome.ToolCallRequested,
                            ExecutionState.Ambiguous,
                            $"The model asked for a tool on the read-only reviewer turn for route "
                                + $"'{requestedRouteId}', so no tool is granted and its answer is not a review. Native termination is unconfirmed." + NoRetryNotice,
                            evidence,
                            promptTokens,
                            completionTokens,
                            observedEvents,
                            contentDeltas,
                            toolCalls,
                            malformed,
                            terminalEventObserved);

                    case StarCliProxyStreamEventKind.Malformed:
                        // Past the first chunk that is known to be wrong, nothing else in the stream can be
                        // trusted, so reading on would be reading on past the error.
                        malformed++;
                        terminalEventObserved = true;

                        return Terminal(
                            requestedRouteId,
                            ReviewChannelTurnOutcome.MalformedStream,
                            ExecutionState.Ambiguous,
                            $"The stream for route '{requestedRouteId}' carried a chunk that is not valid "
                                + "JSON, so the rest of it cannot be interpreted and the turn is not a review.",
                            evidence,
                            promptTokens,
                            completionTokens,
                            observedEvents,
                            contentDeltas,
                            toolCalls,
                            malformed,
                            terminalEventObserved);

                    case StarCliProxyStreamEventKind.Usage:
                        promptTokens = streamEvent.PromptTokens ?? promptTokens;
                        completionTokens = streamEvent.CompletionTokens ?? completionTokens;
                        break;

                    case StarCliProxyStreamEventKind.Completed:
                        // A later DONE marker may repeat stop, but a length/tool/refusal finish is not a complete answer.
                        if (streamEvent.FinishReason is not null && streamEvent.FinishReason != "stop") invalidAnswer = true;
                        stoppedNormally |= streamEvent.FinishReason == "stop";
                        terminalEventObserved = true;
                        break;

                    case StarCliProxyStreamEventKind.Error:
                        invalidAnswer = true;
                        terminalEventObserved = true;
                        terminalError = string.IsNullOrWhiteSpace(streamEvent.ErrorMessage) ? MissingErrorDetails : streamEvent.ErrorMessage;

                        break;
                }
            }
        }
        catch (WorkflowReviewEgressException) { return PolicyRefused(requestedRouteId); }
        catch (WorkflowReviewPreTransportCancellationException)
        {
            return Cancelled(requestedRouteId, "The HTTP client confirmed local cancellation before invoking transport.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Terminal(
                requestedRouteId,
                ReviewChannelTurnOutcome.Cancelled,
                ExecutionState.Ambiguous,
                $"The caller cancelled local reading for route '{requestedRouteId}', but native termination is unconfirmed."
                    + NoRetryNotice,
                evidence,
                promptTokens,
                completionTokens,
                observedEvents,
                contentDeltas,
                toolCalls,
                malformed,
                terminalEventObserved);
        }
        catch (OperationCanceledException)
        {
            // The token that fired is not the caller's - it is the client's own request timeout. This process
            // therefore cannot say whether the request reached the model, and saying "cancelled" would be a
            // claim it has no standing to make.
            _logger.LogWarning(
                "The star-cliproxy reviewer turn for the requested route ended on a timeout this process did "
                + "not request; whether the delivery happened is unknown.");

            return Terminal(
                requestedRouteId,
                ReviewChannelTurnOutcome.UncertainDelivery,
                ExecutionState.Ambiguous,
                $"The read-only reviewer turn for route '{requestedRouteId}' ended on a timeout that was not "
                    + "requested, so whether it reached the model is unknown." + NoRetryNotice,
                evidence,
                promptTokens,
                completionTokens,
                observedEvents,
                contentDeltas,
                toolCalls,
                malformed,
                terminalEventObserved);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The star-cliproxy reviewer turn for the requested route faulted; whether the request reached "
                + "the model before the fault is unknown.");

            return Terminal(
                requestedRouteId,
                ReviewChannelTurnOutcome.UncertainDelivery,
                ExecutionState.Ambiguous,
                $"The read-only reviewer turn for route '{requestedRouteId}' faulted, and whether it reached "
                    + "the model before the fault is unknown." + NoRetryNotice,
                evidence,
                promptTokens,
                completionTokens,
                observedEvents,
                contentDeltas,
                toolCalls,
                malformed,
                terminalEventObserved);
        }

        var report = ClassifyTerminal(
            requestedRouteId,
            terminalError,
            terminalEventObserved,
            contentDeltas,
            evidence,
            promptTokens,
            completionTokens,
            observedEvents,
            toolCalls,
            malformed);
        if (invalidAnswer || !stoppedNormally || terminalError is not null || answer.Length == 0)
            return report;
        try
        {
            // A completed answer can be retained diagnostically while identity remains unproved.
            return report with { Response = new WorkflowModelResponse(answer.ToString()) };
        }
        catch (ArgumentException)
        {
            // UTF-8 bounds / invalid Unicode fail closed; no prefix is promoted to a complete response.
            return report;
        }
    }

    /// <summary>
    /// Local stream observations do not prove native non-delivery or native termination. Unconfirmed
    /// delivery retains execution ownership; even a completed response cannot establish its native route.
    /// </summary>
    private ReviewChannelTurnReport ClassifyTerminal(
        string requestedRouteId,
        string? terminalError,
        bool terminalEventObserved,
        int contentDeltas,
        StarCliProxyObservedEvidence evidence,
        int? promptTokens,
        int? completionTokens,
        int observedEvents,
        int toolCalls,
        int malformed)
    {
        if (terminalError is not null)
        {
            var (outcome, state, reason) = ClassifyError(requestedRouteId, terminalError, contentDeltas);

            return Terminal(
                requestedRouteId,
                outcome,
                state,
                reason,
                evidence,
                promptTokens,
                completionTokens,
                observedEvents,
                contentDeltas,
                toolCalls,
                malformed,
                terminalEventObserved: true);
        }

        if (!terminalEventObserved)
        {
            return Terminal(
                requestedRouteId,
                ReviewChannelTurnOutcome.StreamIncomplete,
                ExecutionState.Ambiguous,
                $"The stream for route '{requestedRouteId}' ended without a terminal event, so the turn said "
                    + "nothing about the artifact it was shown and is not a review of it.",
                evidence,
                promptTokens,
                completionTokens,
                observedEvents,
                contentDeltas,
                toolCalls,
                malformed,
                terminalEventObserved: false);
        }

        var missing = MissingIdentityOf(evidence);

        return Terminal(
            requestedRouteId,
            ReviewChannelTurnOutcome.IdentityNotProvable,
            ExecutionState.Ambiguous,
            missing.Count == 0
                ? $"The turn for route '{requestedRouteId}' completed and the gateway reported a provider, an "
                    + "account, a model and a session, but " + MissingNativeRouteIdentityReason
                    + ". The answer is real and the route it came from is not, so it authorizes nothing."
                : $"The turn for route '{requestedRouteId}' completed and the gateway reported no "
                    + $"{string.Join(", ", missing)} identity, and " + MissingNativeRouteIdentityReason
                    + ". The answer is not attributable to a route and authorizes nothing.",
            evidence,
            promptTokens,
            completionTokens,
            observedEvents,
            contentDeltas,
            toolCalls,
            malformed,
            terminalEventObserved: true);
    }

    private static (ReviewChannelTurnOutcome Outcome, ExecutionState State, string Reason) ClassifyError(
        string requestedRouteId,
        string terminalError,
        int contentDeltas)
    {
        if (terminalError == MissingErrorDetails)
            return (ReviewChannelTurnOutcome.StreamFailed, ExecutionState.Ambiguous,
                "The reviewer stream reported an error without classified details; native termination is unconfirmed." + NoRetryNotice);
        if (StarCliProxyStreamEventMarkers.IsRouteMismatch(terminalError))
        {
            return (
                ReviewChannelTurnOutcome.RouteSubstitution,
                ExecutionState.RouteMismatch,
                $"Local reading for route '{requestedRouteId}' stopped because the evidence the gateway "
                    + "reported contradicted the route that was requested. Native termination is unconfirmed and no part of it is a review." + NoRetryNotice);
        }

        if (StarCliProxyStreamEventMarkers.IsHttpFailure(terminalError))
        {
            // A proxy can fail after forwarding the request. Its HTTP status proves no native termination.
            return (
                ReviewChannelTurnOutcome.GatewayRefused,
                ExecutionState.Ambiguous,
                $"The loopback gateway refused the read-only reviewer turn for route '{requestedRouteId}' with a "
                    + "non-success HTTP status; native delivery and termination are unconfirmed." + NoRetryNotice);
        }

        return contentDeltas == 0
            ? (
                ReviewChannelTurnOutcome.GatewayRefused,
                ExecutionState.Ambiguous,
                $"The stream for route '{requestedRouteId}' reported an error before it produced any content, so "
                    + "no complete answer exists. Native termination is unconfirmed." + NoRetryNotice)
            : (
                ReviewChannelTurnOutcome.StreamFailed,
                ExecutionState.Ambiguous,
                $"The stream for route '{requestedRouteId}' reported an error after it had started, so the turn "
                    + "is incomplete and its partial output is not a review. Native termination is unconfirmed." + NoRetryNotice);
    }

    /// <summary>
    /// Which of the three route dimensions the gateway did not report, in a fixed order. A session id is
    /// deliberately not part of it: a conversation handle identifies a conversation, not a route.
    /// </summary>
    private static IReadOnlyList<string> MissingIdentityOf(StarCliProxyObservedEvidence evidence)
    {
        var missing = new List<string>(3);

        if (evidence.ProviderId is null)
        {
            missing.Add("provider");
        }

        if (evidence.AccountId is null)
        {
            missing.Add("account");
        }

        if (evidence.ModelId is null)
        {
            missing.Add("model");
        }

        return missing;
    }

    private static ReviewChannelTurnReport PolicyRefused(string route) => Refused(route,
        ReviewChannelTurnOutcome.EgressPolicyRefused, ExecutionState.Failed,
        "Workflow review was refused by local stored execution/data policy before HTTP. No prompt was sent.");

    private static ReviewChannelTurnReport Cancelled(string requestedRouteId, string reason) =>
        new(
            ChannelIdValue,
            requestedRouteId,
            ReviewChannelTurnOutcome.Cancelled,
            ExecutionState.Cancelled,
            reason + NoRetryNotice,
            StarCliProxyObservedEvidence.Empty);

    private static ReviewChannelTurnReport Refused(
        string requestedRouteId,
        ReviewChannelTurnOutcome outcome,
        ExecutionState state,
        string reason) =>
        new(
            ChannelIdValue,
            requestedRouteId,
            outcome,
            state,
            reason + NoRetryNotice,
            StarCliProxyObservedEvidence.Empty);

    private static ReviewChannelTurnReport Terminal(
        string requestedRouteId,
        ReviewChannelTurnOutcome outcome,
        ExecutionState state,
        string reason,
        StarCliProxyObservedEvidence evidence,
        int? promptTokens,
        int? completionTokens,
        int observedEvents,
        int contentDeltas,
        int toolCalls,
        int malformed,
        bool terminalEventObserved) =>
        new(
            ChannelIdValue,
            requestedRouteId,
            outcome,
            state,
            reason,
            evidence,
            MissingIdentityOf(evidence),
            evidence.SessionId,
            observedEvents,
            contentDeltas,
            toolCalls,
            malformed,
            terminalEventObserved,
            promptTokens,
            completionTokens);
}

