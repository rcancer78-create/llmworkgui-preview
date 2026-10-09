using System.Runtime.CompilerServices;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// The read-only reviewer channel's boundary, driven by a fake loopback client.
/// <para>
/// The channel exists to refuse truthfully, so the only way to prove it does that is to let it run: a
/// refusal that is never exercised is a claim. Every case here hands the channel a synthetic stream - a
/// non-2xx status, a <c>[DONE]</c> marker with and without identity, a stream that stops, a reported
/// error, a tool call, a malformed chunk, a cancellation and a fault - and asserts two things every time:
/// the turn was never a success, and no route was ever observed. The requested route is a real string in
/// every case, so an implementation that copied it into the observation would fail all of them.
/// </para>
/// <para>
/// The fake client never opens a socket. The stream shapes come from the same normalization the real client
/// produces, including its error prefixes, so what is under test is this channel's classification of a
/// stream rather than the client's ability to read one.
/// </para>
/// </summary>
public sealed class StarCliProxyReviewReadOnlyChannelTests
{
    private const string RealRouteId = "route-real";
    private const string OtherRouteId = "route-other";
    private const string LabelRouteId = "route-opencode";
    private const string ProfileId = "profile-1";
    private const string AccountId = "account-1";
    private const string ModelId = "model-1";
    private const string OtherModelId = "model-2";
    private const string ReviewerRole = "Reviewer";
    private const string StageId = "node-a";
    private const string ApiKey = "sk-proxy-never-logged";

    private static readonly DateTimeOffset ReceivedAt = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("complete", true)]
    [InlineData("length", false)]
    [InlineData("missing-stop", false)]
    [InlineData("late-content", false)]
    [InlineData("late-reasoning", false)]
    [InlineData("error", false)]
    [InlineData("error-without-message", false)]
    [InlineData("too-large", false)]
    [InlineData("unicode-too-large", false)]
    public async Task CapturesOnlyBoundedCompleteAnswerWithoutReasoningOrRouteAuthority(string scenario, bool retained)
    {
        var answer = scenario == "too-large" ? new string('x', 65537)
            : scenario == "unicode-too-large" ? new string('я', 32769) : "Review answer";
        var events = new List<StarCliProxyStreamEvent>
        {
            StarCliProxyStreamEvent.ReasoningDelta("private-reasoning", ReceivedAt),
            StarCliProxyStreamEvent.ContentDelta(answer, ReceivedAt),
            StarCliProxyStreamEvent.Completed(scenario == "length" ? "length" : scenario == "missing-stop" ? null : "stop", ReceivedAt)
        };
        if (scenario == "late-content") events.Add(StarCliProxyStreamEvent.ContentDelta("after-stop", ReceivedAt));
        if (scenario == "late-reasoning") events.Add(StarCliProxyStreamEvent.ReasoningDelta("after-stop", ReceivedAt));
        if (scenario == "error") events.Add(StarCliProxyStreamEvent.Error("late error", ReceivedAt));
        if (scenario == "error-without-message") events.Add(new StarCliProxyStreamEvent { Kind = StarCliProxyStreamEventKind.Error });
        var client = new RecordingClient(events.ToArray());
        var turn = await CreateChannel(client: client).ExecuteTurnAsync(CreateRequest());
        Assert.False(turn.IsSuccess);
        Assert.Null(turn.ObservedRouteId);
        if (retained)
        {
            Assert.Equal(answer, turn.Response!.Content);
            Assert.DoesNotContain("private-reasoning", turn.Response.Content);
        }
        else Assert.Null(turn.Response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorWithoutDetailsIsStreamFailureWithUnconfirmedDelivery(bool afterContent)
    {
        var events = new List<StarCliProxyStreamEvent>();
        if (afterContent) events.Add(StarCliProxyStreamEvent.ContentDelta("partial answer", ReceivedAt));
        events.Add(new StarCliProxyStreamEvent { Kind = StarCliProxyStreamEventKind.Error });
        var report = await CreateChannel(client: new RecordingClient(events.ToArray())).RunAsync(CreateRequest());
        Assert.Equal(ReviewChannelTurnOutcome.StreamFailed, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Null(report.Response);
        Assert.Null(report.ObservedRouteId);
    }

    [Fact]
    public void TheChannelDeclaresTheReviewerCapabilityAndSupportsNoRouteAtAll()
    {
        var channel = CreateChannel();

        Assert.Equal("star-cliproxy-review-readonly", channel.ChannelId);
        Assert.Equal(
            new[] { "workflow.review.readonly" },
            channel.Capabilities.ToArray());

        // Every id this system could plausibly hold: a real-looking row, a second row, a bare label and
        // whitespace. None of them is supported, and the capability list is the whole of what it offers.
        Assert.False(channel.SupportsRoute(RealRouteId));
        Assert.False(channel.SupportsRoute(OtherRouteId));
        Assert.False(channel.SupportsRoute(LabelRouteId));
        Assert.False(channel.SupportsRoute("  "));
    }

    [Fact]
    public void TheRefusalNamesMissingNativeProofDespiteExistingStorageAndSaysNothingWasSent()
    {
        var refusal = CreateChannel().DescribeRouteRefusal(RealRouteId);

        Assert.Contains("native identity storage and a strict route resolver exist", refusal, StringComparison.Ordinal);
        Assert.Contains("trusted per-turn response-origin proof", refusal, StringComparison.Ordinal);
        Assert.Contains("provider, stable account, actual model and route mode or key", refusal, StringComparison.Ordinal);
        Assert.Contains("confirmed terminal outcome", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("stores no native provider id", refusal, StringComparison.Ordinal);

        // And the shape of a refusal: the route it is about, and the fact that nothing happened.
        Assert.Contains($"Route '{RealRouteId}'", refusal, StringComparison.Ordinal);
        Assert.Contains("was not dispatched", refusal, StringComparison.Ordinal);
        Assert.Contains("nothing was recorded", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Succeeded", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogRefusesTheRouteByNameAndCarriesTheChannelsReason()
    {
        var catalog = new WorkflowChannelCatalog(new IWorkflowNodeChannel[] { CreateChannel() });

        var refusal = Assert.Throws<WorkflowValidationException>(
            () => catalog.ResolveChannel(RealRouteId, new[] { "workflow.review.readonly" }));

        // The lookup failure and the reason are both present: an operator can tell that a channel was
        // registered and refused, rather than guessing whether the component is missing.
        Assert.Contains("No channel with proven capabilities", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Declined:", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("star-cliproxy-review-readonly", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Route '{RealRouteId}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("resolved to one persisted Routes.Id", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALabelThatIsNotAPersistedRouteIsNeverSent()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(client: client, routes: new StubRouteRepository());

        var report = await channel.RunAsync(CreateRequest(LabelRouteId));

        Assert.Equal(ReviewChannelTurnOutcome.RouteNotPersisted, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Contains("is not a persisted Routes row", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ARouteWhoseModelIdentityIsGoneIsNeverSent()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(
            client: client,
            routes: new StubRouteRepository(Incomplete(RealRouteId, WorkflowRouteIdentity.Model)));

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.RouteIdentityIncomplete, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Contains("Model", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AWriteTurnIsRefusedBecauseThisChannelDeclaresNothingButTheReadOnlyCapability()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest(isReadOnly: false));

        Assert.Equal(ReviewChannelTurnOutcome.WriteTurnRefused, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Contains("read-only reviewer capability only", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ATurnWhosePromptIsAbsentIsRefusedRatherThanSentEmpty()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest(prompt: null));

        Assert.Equal(ReviewChannelTurnOutcome.PromptAbsent, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AModelTheRoleBindingAndTheRouteDisagreeAboutIsRefusedRatherThanPicked()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest(modelId: OtherModelId));

        // Either choice would review under a model the other side of the pinned assignment did not name.
        Assert.Equal(ReviewChannelTurnOutcome.AssignedModelConflict, report.Outcome);
        Assert.Contains(ModelId, report.Reason, StringComparison.Ordinal);
        Assert.Contains(OtherModelId, report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task NoGatewayBoundToTheRouteMeansNothingIsSent()
    {
        var client = new RecordingClient();
        var channel = CreateChannel(
            client: client,
            gateway: new StubGatewayLocator { Endpoint = null, Reason = "no gateway is bound to any review route" });

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.GatewayNotAddressable, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Contains("no gateway is bound to any review route", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task TheShippedCompositionRefusesEveryRouteAndSendsNothing()
    {
        // The locator the product actually registers, with the real loopback client composed beside it.
        var client = new RecordingClient();
        var channel = CreateChannel(
            client: client,
            gateway: new UnaddressableReviewChannelGatewayLocator());

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.GatewayNotAddressable, report.Outcome);
        Assert.Contains(
            UnaddressableReviewChannelGatewayLocator.NoGatewayBoundReason,
            report.Reason,
            StringComparison.Ordinal);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task NoComposedLoopbackClientMeansNothingIsSent()
    {
        var channel = CreateChannel(gateway: AddressableGateway(), composeClient: false);

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.GatewayClientNotComposed, report.Outcome);
        Assert.Equal(ExecutionState.Failed, report.State);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task TheRequestClaimsNoNativeProviderOrAccountBecauseThisBuildHasNoneToClaim()
    {
        // Our ProviderProfiles.Id and Accounts.Id are not the gateway's provider and account names. Sending
        // them as if they were would be a claim the gateway is certain to contradict, which would turn a
        // missing identity into a route substitution that never happened.
        var client = new RecordingClient();
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        await channel.RunAsync(CreateRequest(nativeSessionId: "native-session-7"));

        var sent = Assert.Single(client.Requests);
        Assert.Null(sent.RequestedProviderId);
        Assert.Null(sent.RequestedAccountId);
        Assert.Equal("native-session-7", sent.RequestedSessionId);
        Assert.Equal(ModelId, sent.ModelId);
        Assert.True(sent.Stream);

        var message = Assert.Single(sent.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("Read-only review request.", message.Content);
    }

    [Fact]
    public async Task ANonSuccessStatusCannotProveNativeNonDelivery()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.Error(
                $"{StarCliProxyStreamEventMarkers.HttpFailurePrefix}503 Service Unavailable.",
                ReceivedAt));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        // A gateway may fail after forwarding; its status cannot release uncertain native ownership.
        Assert.Equal(ReviewChannelTurnOutcome.GatewayRefused, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Contains("non-success HTTP status", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, report.ContentDeltaCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ADoneMarkerWithNoIdentityAtAllIsNeverASuccess()
    {
        var client = new RecordingClient(StarCliProxyStreamEvent.Completed(finishReason: "stop", ReceivedAt));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.True(report.TerminalEventObserved);
        Assert.Equal(new[] { "provider", "account", "model" }, report.MissingIdentity.ToArray());
        Assert.Contains("no provider, account, model identity", report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ADoneMarkerWithAPartialIdentityStillNamesWhatIsMissing()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("first", ReceivedAt),
            StarCliProxyStreamEvent.Usage(120, 40, ReceivedAt, new("codex", null, "gpt-5.5", null)),
            StarCliProxyStreamEvent.Completed("stop", ReceivedAt, new("codex", null, "gpt-5.5", "session-1")));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, report.Outcome);
        Assert.Equal(new[] { "account" }, report.MissingIdentity.ToArray());
        Assert.Equal(120, report.PromptTokens);
        Assert.Equal(40, report.CompletionTokens);
        Assert.Equal("session-1", report.NativeSessionId);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ACompleteIdentityIsStillNotARouteAndTheRequestedRouteIsNeverCopiedIn()
    {
        // The case the whole boundary turns on. The gateway reported everything it reports, the evidence
        // disagrees with nothing, and the turn completed - and the observed route is still null, because
        // provider "codex" plus account "codex-main" plus model "gpt-5.5" is not a Routes.Id and two rows
        // can present exactly that tuple.
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("The document states one thing.", ReceivedAt),
            StarCliProxyStreamEvent.Usage(120, 18, ReceivedAt, FullEvidence()),
            StarCliProxyStreamEvent.Completed("stop", ReceivedAt, FullEvidence()));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Empty(report.MissingIdentity);
        Assert.True(report.ReportedEveryRouteDimension);

        // The requested route is on the report as the audit trail and nowhere else.
        Assert.Equal(RealRouteId, report.RequestedRouteId);
        Assert.Null(report.ObservedRouteId);
        Assert.NotEqual(ExecutionState.Succeeded, report.State);
        Assert.Contains("resolved to one persisted Routes.Id", report.Reason, StringComparison.Ordinal);
        Assert.Contains("authorizes nothing", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoPersistedRoutesWithTheSameTupleStillCannotBeToldApart()
    {
        // Duplicate route tuples, with the rows actually present: two Routes rows, the same provider
        // profile, the same account, the same model, differing only in their mode dimensions. A complete,
        // self-consistent observation cannot say which of the two served it, so the observation stays null
        // and the outcome says why.
        var routes = new StubRouteRepository(
            Complete(RealRouteId),
            Complete(OtherRouteId, reasoningEffort: "high"),
            Complete("route-third", speedMode: "fast"));

        var client = new RecordingClient(StarCliProxyStreamEvent.Completed("stop", ReceivedAt, FullEvidence()));
        var channel = CreateChannel(client: client, routes: routes, gateway: AddressableGateway());

        var first = await channel.RunAsync(CreateRequest());
        var second = await channel.RunAsync(CreateRequest(OtherRouteId));

        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, first.Outcome);
        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, second.Outcome);
        Assert.Null(first.ObservedRouteId);
        Assert.Null(second.ObservedRouteId);
        Assert.Equal(2, client.StreamCallCount);
    }

    [Fact]
    public async Task ConflictingReportedIdentityIsARouteSubstitutionAndNotAFailedReview()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("partial", ReceivedAt),
            StarCliProxyStreamEvent.Error(
                $"{StarCliProxyStreamEventMarkers.RouteMismatchPrefix}Observed model 'gpt-5.5' does not match "
                    + "the requested model 'gpt-5.5-mini'.",
                ReceivedAt,
                FullEvidence()));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.RouteSubstitution, report.Outcome);
        Assert.Equal(ExecutionState.RouteMismatch, report.State);
        Assert.Contains("contradicted the route that was requested", report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AStreamThatSimplyStopsIsNotACompletedTurn()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("half a sentence", ReceivedAt),
            StarCliProxyStreamEvent.ContentDelta(" and then nothing", ReceivedAt));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.StreamIncomplete, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.False(report.TerminalEventObserved);
        Assert.Equal(2, report.ContentDeltaCount);
        Assert.Contains("without a terminal event", report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AnErrorReportedMidStreamRetainsUnconfirmedNativeOwnership()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("an answer that broke", ReceivedAt),
            StarCliProxyStreamEvent.Error("upstream closed the connection", ReceivedAt));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.StreamFailed, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Contains("after it had started", report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AnErrorBeforeAnyContentCannotProveNativeNonDelivery()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.Error("the model is not available", ReceivedAt));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.GatewayRefused, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Contains("before it produced any content", report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AMalformedChunkStopsTheTurnBecauseTheRestCannotBeTrusted()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("readable so far", ReceivedAt),
            StarCliProxyStreamEvent.Malformed("{not json", ReceivedAt),
            StarCliProxyStreamEvent.Completed("stop", ReceivedAt, FullEvidence()));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        // Reading past the first chunk that is known to be wrong would be reading on past the error.
        Assert.Equal(ReviewChannelTurnOutcome.MalformedStream, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Equal(1, report.MalformedEventCount);
        Assert.Equal(1, report.ContentDeltaCount);

        // The completed event that would have followed is never read.
        Assert.Equal(2, report.ObservedEventCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AToolCallIsRefusedBecauseAReadOnlyReviewerHasNoToolToGrant()
    {
        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta("let me look at the file", ReceivedAt),
            StarCliProxyStreamEvent.ToolCall("read_file", "{\"path\":\"a.txt\"}", ReceivedAt),
            StarCliProxyStreamEvent.Completed("tool_calls", ReceivedAt, FullEvidence()));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.ToolCallRequested, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Equal(1, report.ToolCallCount);

        // The turn stops on the tool call: the completed event behind it is never read, and a turn that
        // wanted a tool was never a read-only review of the artifact.
        Assert.Equal(2, report.ObservedEventCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task CancellationInsideTransportCannotProveNativeTermination()
    {
        using var cancelled = new CancellationTokenSource();
        var client = new RecordingClient
        {
            // The caller token fired inside transport; that does not confirm native termination.
            Fault = token =>
            {
                cancelled.Cancel();

                return new OperationCanceledException(token);
            }
        };

        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest(), cancelled.Token);

        Assert.Equal(ReviewChannelTurnOutcome.Cancelled, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Contains("not retried", report.Reason, StringComparison.Ordinal);

        // One delivery, and no second attempt: a retry would be a second delivery of the same request with
        // no record of whether the first one landed.
        Assert.Equal(1, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ACancellationRequestedBeforeAnythingIsSentNeverReachesTheGateway()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var client = new RecordingClient();
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest(), cancelled.Token);

        Assert.Equal(ReviewChannelTurnOutcome.Cancelled, report.Outcome);
        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Equal(0, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task AFaultIsAnUncertainDeliveryAndIsNotReportedAsACancellation()
    {
        // This process cannot know whether the request reached the model before the fault, so the outcome
        // says exactly that instead of splitting one unknown into several guesses.
        var client = new RecordingClient { Fault = _ => new HttpRequestException("the loopback connection reset") };
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.UncertainDelivery, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        Assert.Contains("whether it reached the model", report.Reason, StringComparison.Ordinal);
        Assert.Contains("not retried", report.Reason, StringComparison.Ordinal);
        Assert.Equal(1, client.StreamCallCount);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task ATimeoutThisProcessDidNotRequestIsAnUncertainDeliveryAndNotACancellation()
    {
        // The token that fired is the client's own request timeout, not the caller's. Reporting that as a
        // cancellation would be a claim about the delivery this process has no standing to make.
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();

        var client = new RecordingClient { Fault = _ => new OperationCanceledException(unrelated.Token) };
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var report = await channel.RunAsync(CreateRequest());

        Assert.Equal(ReviewChannelTurnOutcome.UncertainDelivery, report.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, report.State);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task TheReportKeepsCountsAndIdentifiersButNeverOutputOrACredential()
    {
        // A diagnostic must not become a second copy of the reviewed document, and must not carry the
        // gateway key that lives in memory only.
        const string answer = "the secret contents of the reviewed document";

        var client = new RecordingClient(
            StarCliProxyStreamEvent.ContentDelta(answer, ReceivedAt),
            StarCliProxyStreamEvent.ReasoningDelta("thinking about the answer", ReceivedAt),
            StarCliProxyStreamEvent.Completed("stop", ReceivedAt, FullEvidence()));

        var report = await CreateChannel(client: client, gateway: AddressableGateway())
            .RunAsync(CreateRequest());

        Assert.Equal(2, report.ContentDeltaCount);
        Assert.Equal("codex", report.Evidence.ProviderId);
        Assert.Equal("codex-main", report.Evidence.AccountId);
        Assert.Equal("gpt-5.5", report.Evidence.ModelId);

        // The four-argument fixture stays unverified, so reporting every identifier the gateway
        // mentioned still cannot read as a proven route.
        Assert.False(report.Evidence.IsProviderVerified);
        Assert.False(report.Evidence.IsAccountVerified);
        Assert.False(report.Evidence.IsModelVerified);
        Assert.False(report.Evidence.IsSessionVerified);
        Assert.DoesNotContain(answer, report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("thinking about the answer", report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, report.Reason, StringComparison.Ordinal);
        AssertNoRouteAndNoSuccess(report);
    }

    [Fact]
    public async Task TheTurnResultCarriesTheStateTheGateReadsAndStillNeverARoute()
    {
        var client = new RecordingClient(StarCliProxyStreamEvent.Completed("stop", ReceivedAt, FullEvidence()));
        var channel = CreateChannel(client: client, gateway: AddressableGateway());

        var result = await channel.ExecuteTurnAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionState.Ambiguous, result.State);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal("session-1", result.NativeSessionId);
    }

    private static void AssertNoRouteAndNoSuccess(ReviewChannelTurnReport report)
    {
        Assert.NotEqual(ExecutionState.Succeeded, report.State);
        Assert.Null(report.ObservedRouteId);
    }

    private static StarCliProxyObservedEvidence FullEvidence() => new("codex", "codex-main", "gpt-5.5", "session-1");

    private static Route CreateRoute(
        string routeId,
        string? reasoningEffort = null,
        string? speedMode = null) =>
        new(
            routeId,
            new SessionBinding(
                BackendType.StarCliProxy,
                ProfileId,
                AccountId,
                ModelId,
                reasoningEffort,
                speedMode,
                executionMode: null),
            DataClassification.PrivateSource,
            isEnabled: true,
            HealthState.Healthy,
            manualPriority: 0);

    private static WorkflowRouteAssignment Complete(
        string routeId,
        string? reasoningEffort = null,
        string? speedMode = null) =>
        WorkflowRouteAssignment.Complete(CreateRoute(routeId, reasoningEffort, speedMode));

    private static WorkflowRouteAssignment Incomplete(string routeId, WorkflowRouteIdentity missing) =>
        new(CreateRoute(routeId), new[] { missing });

    private static StarCliProxyReviewReadOnlyChannel CreateChannel(
        RecordingClient? client = null,
        StubRouteRepository? routes = null,
        IReviewChannelGatewayLocator? gateway = null,
        bool composeClient = true) =>
        new(
            routes ?? new StubRouteRepository(Complete(RealRouteId)),
            gateway ?? AddressableGateway(),
            composeClient ? client ?? new RecordingClient() : null,
            egressPolicy: new FixtureProtocolPolicy());

    // Synthetic policy only for stream classification; real SQLite/HTTP admission is tested separately.
    private sealed class FixtureProtocolPolicy : LLMWorkGUI.Application.Security.IWorkflowReviewEgressPolicy
    {
        public Task<string> ValidateAsync(WorkflowReviewEgressContext context, string endpoint, string prompt,
            string? reasoningEffort, string? nativeSessionId, CancellationToken token) => Task.FromResult(ModelId);
        public Task AuthorizeAsync(WorkflowReviewEgressContext context, string endpoint, string body,
            string? nativeSessionId, CancellationToken token) => throw new NotSupportedException("Fixture RecordingClient has no HTTP transport.");
    }

    private static StubGatewayLocator AddressableGateway() =>
        new() { Endpoint = new StarCliProxyEndpoint(new Uri("http://127.0.0.1:8317/"), ApiKey) };

    private static WorkflowChannelTurnRequest CreateRequest(
        string routeId = RealRouteId,
        string? modelId = ModelId,
        string? prompt = "Read-only review request.",
        bool isReadOnly = true,
        string? nativeSessionId = null) =>
        new(
            "execution-1",
            new WorkflowNodeDefinition(
                StageId,
                WorkflowNodeKind.Review,
                "Node A",
                ReviewerRole,
                successTargetNodeId: "node-b",
                gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.DocumentReview,
                    new[] { ReviewerRole },
                    requiresUserApproval: false,
                    artifactRequirement: "ReviewedDocument")),
            new RoleBindingDefinition(ReviewerRole, routeId, modelId: modelId),
            routeId,
            isReadOnly,
            prompt,
            nativeSessionId);

    /// <summary>The route store, answered from memory. It is a store double, not a mapper: nothing here can
    /// turn an observation into a route, which is exactly what the production store cannot do either.</summary>
    private sealed class StubRouteRepository : IRouteRepository
    {
        private readonly Dictionary<string, WorkflowRouteAssignment> _routes = new(StringComparer.Ordinal);

        public StubRouteRepository(params WorkflowRouteAssignment[] assignments)
        {
            foreach (var assignment in assignments)
            {
                _routes[assignment.Route.Id] = assignment;
            }
        }

        public Task<WorkflowRouteAssignment?> GetAssignmentAsync(
            string routeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_routes.TryGetValue(routeId, out var assignment) ? assignment : null);
    }

    private sealed class StubGatewayLocator : IReviewChannelGatewayLocator
    {
        public StarCliProxyEndpoint? Endpoint { get; init; }

        public string Reason { get; init; } = "no gateway is bound to this route";

        public ReviewChannelGatewayLocation Locate(WorkflowRouteAssignment assignment) =>
            Endpoint is null ? ReviewChannelGatewayLocation.None(Reason) : ReviewChannelGatewayLocation.At(Endpoint);
    }

    /// <summary>
    /// The loopback client, answered from memory. It records what it was asked to send, so a test can prove
    /// that a refused turn reached nothing and that a sent turn claimed no native identity it does not
    /// have.
    /// </summary>
    private sealed class RecordingClient : IStarCliProxyClient
    {
        private readonly IReadOnlyList<StarCliProxyStreamEvent> _events;

        public RecordingClient(params StarCliProxyStreamEvent[] events) => _events = events;

        public List<StarCliProxyChatRequest> Requests { get; } = new();

        /// <summary>
        /// Thrown instead of the stream, from the caller's own token so a cancellation can be distinguished
        /// from a timeout the channel never asked for.
        /// </summary>
        public Func<CancellationToken, Exception>? Fault { get; init; }

        public int StreamCallCount { get; private set; }

        public Task<IReadOnlyList<StarCliProxyModelInfo>> ListModelsAsync(
            StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A reviewer turn never lists models.");

        public async IAsyncEnumerable<StarCliProxyStreamEvent> StreamChatCompletionAsync(
            StarCliProxyEndpoint endpoint,
            StarCliProxyChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCallCount++;
            Requests.Add(request);

            if (Fault is not null)
            {
                await Task.Yield();
                throw Fault(cancellationToken);
            }

            foreach (var streamEvent in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Yield();

                yield return streamEvent;
            }
        }

        public Task<StarCliProxyHealthStatus> CheckHealthAsync(
            StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A reviewer turn never probes health.");
    }
}

