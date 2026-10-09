using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// The assigned-model-review request: what it resolves, what it refuses, and what it is allowed to persist.
/// <para>
/// Every refusal here happens before a row is written, which is the property that keeps a refused request
/// from leaving a pending reviewer turn behind for a later read to mistake for one in progress. The tests
/// assert that directly - a refusal must leave the session store, the execution store and the evidence store
/// exactly as it found them - because a "refused" request that still wrote a queued execution is the failure
/// mode that would make the product's status a lie.
/// </para>
/// <para>
/// The channel here is a deterministic test double and only ever reaches the dispatch boundary with an
/// observed route the test itself declares. Production registers no channel at all, which is why the shipped
/// product refuses; that refusal is covered by
/// <see cref="WithNoChannelAtAllTheWholeRequestIsRefused"/>.
/// </para>
/// </summary>
public sealed partial class WorkflowReviewRequestServiceTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string TemplateId = "review-template";
    private const string StageId = "node-a";
    private const string TerminalStageId = "node-b";
    private const string ArtifactKind = "ReviewedDocument";
    private const string ReviewerRole = "Reviewer";
    private const string RealRouteId = "route-real";
    private const string DocumentedDefaultRouteId = "route-opencode";
    private const string ArtifactContent = "the document under review";

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryWorkflowRunRepository _runs = new();
    private readonly InMemoryWorkflowTemplateStore _templates = new();
    private readonly InMemoryWorkflowArtifactBlobStore _blobs = new();
    private readonly InMemoryRouteRepository _routes = new();
    private readonly InMemoryProjectRepository _projects = new();
    private readonly InMemorySessionRepository _sessions = new();

    // The evidence store is given the execution store on purpose: an observed route is only ever what the
    // execution row recorded, so a double that could be handed a route the execution never reported would
    // prove a gate the product refuses.
    private readonly InMemoryExecutionRepository _executions = new();
    private readonly InMemoryReviewEvidenceRepository _evidence;
    private readonly FakeTimeProvider _time = new(BaseTime);

    public WorkflowReviewRequestServiceTests()
    {
        _evidence = new InMemoryReviewEvidenceRepository(_executions);

        _projects.Add(new Project(
            ProjectId,
            "Project",
            @"C:\project",
            null,
            false,
            true,
            null,
            null,
            DataClassification.PrivateSource));
    }

    [Fact]
    public async Task TheExistingRouteOpenCodeLabelIsRefusedAndNoRouteRowIsCreatedForIt()
    {
        // The literal the Studio's standard template binds to every role. It has never been a Routes row, and
        // the refusal must not turn into inserting one: the executions table's own foreign key would reject
        // it, and creating it to make the request succeed would be inventing the assignment it claims to read.
        var service = CreateService();

        var result = await service.RequestAssignedReviewAsync(
            await StartRunAsync(DocumentedDefaultRouteId));

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Equal(DocumentedDefaultRouteId, role.AssignedRouteId);
        Assert.Contains("is not a persisted route", role.Refusal!, StringComparison.Ordinal);
        Assert.Empty(_routes.Written);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task WithNoChannelAtAllTheWholeRequestIsRefused()
    {
        // This is the shipped product's actual state: the catalog is registered empty, because nothing in
        // this build reports an independently observed route. The refusal has to arrive before any write.
        var service = CreateService(withChannel: false);

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Equal(RealRouteId, role.AssignedRouteId);
        Assert.Contains("No channel with proven capabilities", role.Refusal!, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task ARealRouteWithEveryIdentityPersistsASessionAnExecutionAndABinding()
    {
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId);

        var result = await service.RequestAssignedReviewAsync(runId);

        var role = Assert.Single(result.Roles);
        Assert.Equal(WorkflowReviewRequestOutcome.Dispatched, result.Outcome);
        Assert.Null(role.Refusal);
        Assert.Equal(RealRouteId, role.AssignedRouteId);
        Assert.True(role.ReadOnly);
        Assert.True(role.BoundToArtifact);

        // The observed route is what the channel reported, and the requested route was persisted before the
        // turn existed at all.
        Assert.Equal(RealRouteId, role.ObservedRouteId);
        Assert.Equal(ExecutionState.Succeeded, role.ExecutionState);
        Assert.True(role.ObservedAssignedRoute);

        var session = Assert.Single(_sessions.Sessions);
        Assert.Equal(runId, session.WorkflowRunId);
        Assert.Equal(ReviewerRole, session.Role);

        var execution = Assert.Single(_executions.Executions);
        Assert.Equal(RealRouteId, execution.RequestedRouteId);
        Assert.Equal(RealRouteId, execution.ObservedRouteId);
        Assert.Equal(ExecutionState.Succeeded, execution.State);

        var evidence = Assert.Single(await _evidence.ListByRunIdAsync(runId));
        Assert.True(evidence.IsReadOnly);
        Assert.Equal(RealRouteId, evidence.RequestedRouteId);
        Assert.Equal(RealRouteId, evidence.ObservedRouteId);
        Assert.Equal(StageId, evidence.StageId);
        Assert.Equal(ReviewerRole, evidence.ReviewerRole);
    }

    [Fact]
    public async Task ARouteWhoseModelOrAccountIsMissingIsRefusedByName()
    {
        _routes.Assign(
            RealRouteId,
            missing: new[] { WorkflowRouteIdentity.Model });
        var service = CreateService();

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Contains("is missing the required Model identity", role.Refusal!, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task ADisabledRouteIsRefused()
    {
        _routes.Assign(RealRouteId, enabled: false);
        var service = CreateService();

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Contains("is disabled", role.Refusal!, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task ATurnThatReportedNoRouteIsPersistedAsUnobservedAndSatisfiesNothing()
    {
        var service = CreateService(channel: new StubChannel { State = ExecutionState.Succeeded, ObservedRouteId = null });

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.Null(role.Refusal);
        Assert.Equal(ExecutionState.RouteMismatch, role.ExecutionState);
        Assert.Null(role.ObservedRouteId);
        Assert.False(role.ObservedAssignedRoute);

        // The requested route is still recorded - it was genuinely requested - and the observed column stays
        // empty, which is what a turn that reported nothing has to look like afterwards.
        var execution = Assert.Single(_executions.Executions);
        Assert.Equal(RealRouteId, execution.RequestedRouteId);
        Assert.Null(execution.ObservedRouteId);

        var evidence = Assert.Single(await _evidence.ListByRunIdAsync(result.RunId));
        Assert.Null(evidence.ObservedRouteId);
        Assert.Equal(ExecutionState.RouteMismatch, evidence.ExecutionState);
    }

    [Fact]
    public async Task ATurnThatReportedAnotherRouteKeepsThatRouteAndIsAMismatch()
    {
        // The backend really used a different route than the one that was requested. The route that was used
        // is what is stored; the requested one is left alone beside it.
        _routes.Assign("route-other");
        var service = CreateService(channel: new StubChannel { State = ExecutionState.Succeeded, ObservedRouteId = "route-other" });

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.Equal(ExecutionState.RouteMismatch, role.ExecutionState);
        Assert.Equal("route-other", role.ObservedRouteId);
        Assert.False(role.ObservedAssignedRoute);

        // The route that was really used is kept on the execution, not overwritten with the requested one.
        var execution = Assert.Single(_executions.Executions);
        Assert.Equal(RealRouteId, execution.RequestedRouteId);
        Assert.Equal("route-other", execution.ObservedRouteId);
    }

    [Fact]
    public async Task ACancelledTransportWithoutNativeConfirmationIsAmbiguousAndNeverRetried()
    {
        var channel = new StubChannel { Failure = new OperationCanceledException("cancelled") };
        var service = CreateService(channel: channel);

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.Equal(ExecutionState.Ambiguous, role.ExecutionState);
        Assert.Null(role.ObservedRouteId);
        Assert.False(role.ObservedAssignedRoute);

        // One delivery was attempted and one execution row exists. The two writes are the queued row written
        // before dispatch and the same row completed afterwards - never a second execution and never a retry.
        Assert.Single(channel.Requests);
        Assert.Single(_executions.Executions);
    }

    [Fact]
    public async Task AnUnexpectedFaultIsPersistedAsAmbiguousBecauseDeliveryCannotBeKnown()
    {
        // The process cannot know whether the request reached the model before the fault, and an ambiguous
        // delivery must not become a success - nor a retry, which would be a second delivery of the same
        // request with no record of whether the first one landed.
        var channel = new StubChannel { Failure = new InvalidOperationException("transport closed") };
        var service = CreateService(channel: channel);

        var result = await service.RequestAssignedReviewAsync(await StartRunAsync(RealRouteId));

        var role = Assert.Single(result.Roles);
        Assert.Equal(ExecutionState.Ambiguous, role.ExecutionState);
        Assert.Null(role.ObservedRouteId);
        Assert.Single(channel.Requests);
        Assert.Single(_executions.Executions);

        var evidence = Assert.Single(await _evidence.ListByRunIdAsync(result.RunId));
        Assert.Equal(ExecutionState.Ambiguous, evidence.ExecutionState);
    }

    [Fact]
    public async Task ARequestIsRefusedWhenTheArtifactBytesAreGone()
    {
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId);

        // The row says the artifact is current and the store says its content is not there. A reviewer is
        // never shown bytes that cannot be re-hashed, and no execution is recorded for a turn that never had
        // content.
        foreach (var blobId in _blobs.BlobIds)
        {
            _blobs.Delete(blobId);
        }

        var result = await service.RequestAssignedReviewAsync(runId);

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Contains("cannot be re-hashed and opened", role.Refusal!, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task ARequestIsRefusedWhenTheArtifactIsNotUtf8Text()
    {
        // A rendering built from replacement characters is not the artifact, and a model asked to approve
        // bytes it did not really receive would be approving nothing.
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId, content: new byte[] { 0xC3, 0x28, 0xA0, 0xA1 });

        var result = await service.RequestAssignedReviewAsync(runId);

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Contains("cannot be re-hashed and opened", role.Refusal!, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task AStreamThatYieldsOtherBytesThanTheVerifiedArtifactRefusesTheWholeRequest()
    {
        // The race a verify-then-read contract cannot close on its own. The store re-hashed the committed
        // bytes and then opened a second, independent handle on the same address, and the handle it handed
        // back carries different content - the exact gap between "the artifact verified" and "these are the
        // bytes the reviewer is shown". Both contents are valid UTF-8 and both fit the bound, so nothing but
        // hashing the copied bytes can tell them apart.
        var channel = new StubChannel();
        var service = CreateService(channel: channel);
        var runId = await StartRunAsync(RealRouteId);
        var blobId = Assert.Single(_blobs.BlobIds);

        _blobs.SwapOnNextOpen(blobId, Encoding.UTF8.GetBytes("a different document, swapped in after verification"));

        var result = await service.RequestAssignedReviewAsync(runId);

        var role = Assert.Single(result.Roles);
        Assert.True(result.IsRefusal);
        Assert.Contains("cannot be re-hashed and opened", role.Refusal!, StringComparison.Ordinal);

        // Nothing at all happened: no turn was dispatched, no session, no execution, no binding, and no
        // verdict. A request whose content could not be established cannot be half-recorded either.
        Assert.Empty(channel.Requests);
        AssertNothingWasPersisted();

        // The swap is a one-shot, so the same run dispatches normally afterwards - which is what makes the
        // refusal above a statement about the substituted content and not about the fixture.
        var retried = await service.RequestAssignedReviewAsync(runId);

        Assert.Equal(WorkflowReviewRequestOutcome.Dispatched, retried.Outcome);
        Assert.Single(_sessions.Sessions);
        Assert.Single(_executions.Executions);
    }

    [Fact]
    public async Task ASecondRequestForTheSameRoleAndArtifactIsRefusedAsADuplicate()
    {
        // One binding per run, stage, role and reviewed hash, so a replayed request is refused before anything
        // is written rather than discovered at insert time - which would leave a pending reviewer turn behind
        // for a request that was in fact refused.
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId);

        Assert.Equal(WorkflowReviewRequestOutcome.Dispatched, (await service.RequestAssignedReviewAsync(runId)).Outcome);

        var replay = await service.RequestAssignedReviewAsync(runId);

        Assert.True(replay.IsRefusal);
        var role = Assert.Single(replay.Roles);
        Assert.Contains("is a duplicate request and nothing is dispatched", role.Refusal!, StringComparison.Ordinal);

        // One execution, one session, one binding: the refusal added nothing.
        Assert.Single(_executions.Executions);
        Assert.Single(_sessions.Sessions);
        Assert.Single(await _evidence.ListByRunIdAsync(runId));
    }

    [Theory]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.Cancelled)]
    public async Task ExplicitRetryAfterKnownFailure_AlsoWorksForRepositoryOnlyComposition(ExecutionState state)
    {
        var channel = new StubChannel { State = state };
        var service = CreateService(channel: channel);
        var runId = await StartRunAsync(RealRouteId);
        Assert.False((await service.RequestAssignedReviewAsync(runId)).IsRefusal);
        var first = Assert.Single(_executions.Executions);
        channel.State = ExecutionState.Succeeded;
        Assert.False((await service.RequestAssignedReviewAsync(runId)).IsRefusal);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal(2, (await _evidence.ListByRunIdAsync(runId)).Count);
        Assert.True((await service.RequestAssignedReviewAsync(runId)).IsRefusal);
    }

    [Fact]
    public async Task AReplacedArtifactMakesTheRequestAboutTheNewBytesOnly()
    {
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId);

        var replacement = await _blobs.SaveAsync(new MemoryStream("a different revision"u8.ToArray(), writable: false));

        var beforeReplacement = (await _runs.GetByIdAsync(runId))!;
        var replacementEvidence = new WorkflowArtifactEvidence(
            "artifact-2",
            runId,
            StageId,
            ArtifactKind,
            replacement.BlobId,
            replacement.BlobId,
            BaseTime.AddMinutes(2),
            replacement.SizeBytes,
            DataClassification.PrivateSource);

        await _runs.SaveArtifactAsync(beforeReplacement.WithArtifact(replacementEvidence), replacementEvidence);

        var result = await service.RequestAssignedReviewAsync(runId);

        var role = Assert.Single(result.Roles);
        Assert.Null(role.Refusal);
        Assert.Equal(replacement.BlobId, result.CurrentArtifactHash);

        var evidence = Assert.Single(await _evidence.ListByRunIdAsync(runId));
        Assert.Equal(replacement.BlobId, evidence.ReviewedArtifactHash);
        Assert.Equal("artifact-2", evidence.ReviewedArtifactId);
    }

    [Fact]
    public async Task ALegacyRunIsRefusedBecauseItDeclaresNoPinnedAssignment()
    {
        var service = CreateService();

        var runService = new WorkflowRunService(
            _runs,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            _templates,
            _blobs,
            new FixedUserApprovalIdentity("DOMAIN\\operator"),
            _time,
            _evidence);

        var legacy = await runService.StartLegacyRunAsync(ProjectId, PackageId, VersionId);

        var result = await service.RequestAssignedReviewAsync(legacy.Id);

        Assert.True(result.IsRefusal);
        Assert.Empty(result.Roles);
        Assert.Contains("not pinned to a template version", result.Detail, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task AStageThatDeclaresNoReviewerRolesIsRefused()
    {
        var service = CreateService();

        var runId = await StartRunAsync(RealRouteId, reviewerRoles: Array.Empty<string>());

        var result = await service.RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        Assert.Contains("declares no required reviewer roles", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStageWithNoStoredArtifactIsRefused()
    {
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId, recordArtifact: false);

        var result = await service.RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        Assert.Contains("No stored 'ReviewedDocument' artifact", result.Detail, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task AnUnreadablePinnedSchemeIsRefused()
    {
        var service = CreateService();
        var runId = await StartRunAsync(RealRouteId);
        _runs.CorruptSchemeSnapshot(runId);

        var result = await service.RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        Assert.Contains("pinned scheme of workflow run", result.Detail, StringComparison.Ordinal);
        AssertNothingWasPersisted();
    }

    private void AssertNothingWasPersisted()
    {
        Assert.Empty(_sessions.Sessions);
        Assert.Empty(_executions.Executions);
        Assert.Empty(_evidence.Saved);
        Assert.Empty(_runs.Runs.SelectMany(run => run.Verdicts));
    }

    /// <summary>
    /// A run pinned to a one-node reviewer gate, with the template's role bound to
    /// <paramref name="primaryRouteId"/> and - unless the test says otherwise - a stored, verified artifact
    /// for that stage.
    /// </summary>
    private async Task<string> StartRunAsync(
        string primaryRouteId,
        IReadOnlyList<string>? reviewerRoles = null,
        bool recordArtifact = true,
        byte[]? content = null)
    {
        await _templates.SaveAsync(CreateTemplate(primaryRouteId, reviewerRoles));
        await _templates.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", ProjectId, TemplateId, 1, BaseTime));

        var runService = new WorkflowRunService(
            _runs,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            _templates,
            _blobs,
            new FixedUserApprovalIdentity("DOMAIN\\operator"),
            _time,
            _evidence);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);

        if (!recordArtifact)
        {
            return run.Id;
        }

        var bytes = content ?? Encoding.UTF8.GetBytes(ArtifactContent);
        var blob = await _blobs.SaveAsync(new MemoryStream(bytes, writable: false));

        // The run that carries the evidence is the copy: the repository is handed the run it is about to
        // publish, and the run the caller still holds is the one without the artifact.
        var firstEvidence = new WorkflowArtifactEvidence(
            "artifact-1",
            run.Id,
            StageId,
            ArtifactKind,
            blob.BlobId,
            blob.BlobId,
            BaseTime.AddMinutes(1),
            blob.SizeBytes,
            DataClassification.PrivateSource);

        await _runs.SaveArtifactAsync(run.WithArtifact(firstEvidence), firstEvidence);

        return run.Id;
    }

    private WorkflowReviewRequestService CreateService(
        bool withChannel = true,
        IWorkflowNodeChannel? channel = null)
    {
        // Only when the test has not already described the route itself: a test that assigns a missing
        // identity or a disabled flag must not have that replaced by the default.
        _routes.AssignIfAbsent(RealRouteId);

        var channels = channel is not null
            ? new IWorkflowNodeChannel[] { channel }
            : withChannel
                ? new IWorkflowNodeChannel[] { new StubChannel() }
                : Array.Empty<IWorkflowNodeChannel>();

        var profiles = new LLMWorkGUI.Application.Tests.Routing.InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile("profile-1", "Profile", BackendType.OpenCode, null, null,
            DataClassification.PrivateSource, true));

        return new WorkflowReviewRequestService(
            _runs,
            _templates,
            _evidence,
            _routes,
            _projects,
            _sessions,
            _executions,
            new WorkflowChannelCatalog(channels),
            _blobs,
            _time,
            dataClassificationGate: new LLMWorkGUI.Application.Security.DataClassificationGate(profiles),
            secretScanner: new CleanReviewScanner());
    }

    private sealed class CleanReviewScanner : IWorkflowSecretScanner
    {
        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkflowSecretScanReport.Empty);
    }

    private static WorkflowTemplateDefinition CreateTemplate(
        string primaryRouteId,
        IReadOnlyList<string>? reviewerRoles) =>
        new(
            TemplateId,
            1,
            "Review template",
            "A two-node chain whose first node is a reviewer gate.",
            new WorkflowGraph(
                StageId,
                new[]
                {
                    new WorkflowNodeDefinition(
                        StageId,
                        WorkflowNodeKind.Prompt,
                        "Node A",
                        "Reviewer",
                        successTargetNodeId: TerminalStageId,
                        gateMetadata: new WorkflowNodeGateMetadata(
                            WorkflowStageKind.DocumentReview,
                            reviewerRoles ?? new[] { ReviewerRole },
                            requiresUserApproval: false,
                            artifactRequirement: ArtifactKind)),
                    new WorkflowNodeDefinition(
                        TerminalStageId,
                        WorkflowNodeKind.TerminalOutcome,
                        "Node B",
                        "Coordinator")
                }),
            new[]
            {
                new RoleBindingDefinition(ReviewerRole, primaryRouteId, modelId: "model-1")
            },
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            BaseTime);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    /// <summary>
    /// A reviewer channel whose every answer the test declares. It never derives the observed route from the
    /// requested one: a null observation stays null, which is the whole point of having this be a double
    /// rather than a convenience in production.
    /// </summary>
    private sealed class StubChannel : IWorkflowNodeChannel
    {
        public string ChannelId => "stub-reviewer";

        public IReadOnlyList<string> Capabilities { get; set; } =
            new[] { WorkflowReviewRequestService.ReviewerCapability };

        public HashSet<string> Routes { get; set; } = new(StringComparer.Ordinal) { RealRouteId };

        public ExecutionState State { get; set; } = ExecutionState.Succeeded;

        public string? ObservedRouteId { get; set; } = RealRouteId;

        public Exception? Failure { get; set; }

        public List<WorkflowChannelTurnRequest> Requests { get; } = new();

        public bool SupportsRoute(string routeId) => Routes.Contains(routeId);

        public Task<WorkflowChannelTurnResult> ExecuteTurnAsync(
            WorkflowChannelTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new WorkflowChannelTurnResult(State, ObservedRouteId));
        }
    }
}
