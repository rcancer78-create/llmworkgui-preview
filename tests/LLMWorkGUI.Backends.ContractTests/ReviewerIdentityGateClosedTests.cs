using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

/// <summary>
/// The gate this phase is not allowed to open.
/// <para>
/// The native identity foundation stores four facts and resolves them, and the read-only reviewer channel
/// still refuses every route because the gateway has never been observed reporting a response-origin
/// identity. Nothing about storing a name or resolving one proves that a gateway will report one, so the
/// only honest reading of this diff leaves the refusal exactly where it was.
/// </para>
/// <para>
/// The interesting part is that the refusal is asserted, not assumed. <c>SupportsRoute</c> is a constant
/// <c>false</c> and a constant would survive an edit that quietly made it depend on the new storage, so the
/// refusal is driven here through a fully bound library, and the resolver is shown to be reachable from no
/// production container at all.
/// </para>
/// </summary>
public sealed class ReviewerIdentityGateClosedTests
{
    private const string RouteId = "route-observed";

    /// <summary>
    /// The four facts migration 012 stores, filled in completely. A fully bound library is the strongest
    /// state this phase can produce, so it is the state the refusal is tested against.
    /// </summary>
    private static StarCliProxyReviewReadOnlyChannel BoundChannel() =>
        new(
            new StubRouteRepository(
                WorkflowRouteAssignment.Complete(new Route(
                    RouteId,
                    new SessionBinding(
                        BackendType.StarCliProxy,
                        "profile-1",
                        "account-1",
                        "model-1",
                        reasoningEffort: "high",
                        speedMode: "fast",
                        executionMode: "exec"),
                    DataClassification.PrivateSource,
                    isEnabled: true,
                    HealthState.Healthy,
                    manualPriority: 0,
                    gatewayRouteKey: "gw-route-key"))),
            new UnaddressableReviewChannelGatewayLocator());

    [Fact]
    public void TheChannelStillSupportsNoRouteEvenWhenTheRouteCarriesEveryGatewayIdentity()
    {
        var channel = BoundChannel();

        // The route exists, resolves every identity, is enabled, is healthy and declares a gateway route key.
        // It is still not supported, because support would mean a dispatch decision and this phase makes
        // none.
        Assert.False(channel.SupportsRoute(RouteId));
        Assert.False(channel.SupportsRoute("route-opencode"));
        Assert.False(channel.SupportsRoute("anything"));
    }

    [Fact]
    public void TheRefusalStillNamesTheFourIdentitiesAndStillSaysNothingWasDispatched()
    {
        var refusal = BoundChannel().DescribeRouteRefusal(RouteId);

        Assert.Contains("resolved to one persisted Routes.Id", refusal, StringComparison.Ordinal);
        Assert.Contains($"Route '{RouteId}'", refusal, StringComparison.Ordinal);
        Assert.Contains("was not dispatched", refusal, StringComparison.Ordinal);
        Assert.Contains("nothing was recorded", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Succeeded", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogStillRefusesTheRouteEvenWithAFullyBoundRouteBehindIt()
    {
        var catalog = new WorkflowChannelCatalog(
            new IWorkflowNodeChannel[] { BoundChannel() });

        var refusal = Assert.Throws<WorkflowValidationException>(
            () => catalog.ResolveChannel(RouteId, new[] { WorkflowReviewRequestService.ReviewerCapability }));

        Assert.Contains("No channel with proven capabilities", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("star-cliproxy-review-readonly", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTurnStillReportsNoObservedRouteAndNoSuccess()
    {
        var report = await BoundChannel().RunAsync(new WorkflowChannelTurnRequest(
            "execution-1",
            new WorkflowNodeDefinition(
                "node-a",
                WorkflowNodeKind.Review,
                "Node A",
                "Reviewer",
                successTargetNodeId: "node-b",
                gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.DocumentReview,
                    new[] { "Reviewer" },
                    requiresUserApproval: false,
                    artifactRequirement: "ReviewedDocument")),
            new RoleBindingDefinition("Reviewer", RouteId, modelId: "model-1"),
            RouteId,
            isReadOnly: true,
            promptOrCommand: "Read-only review request."));

        // The gateway is unaddressable on this host, so the turn is refused before anything is sent, and the
        // report says so with a null observed route. There is no path from the new storage to this value.
        Assert.Null(report.ObservedRouteId);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.NotEqual(ExecutionState.Succeeded, report.State);
        Assert.Contains("Nothing was sent", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoProductionContainerRegistersTheDiagnosticResolverOrItsReader()
    {
        // The reader is the only way to obtain a persisted candidate and the resolver is the only thing that
        // turns a candidate into a route id. Composing neither anywhere means no channel, executor, run
        // service or gate can ask for a route id, so the review gate cannot be opened by a future edit that
        // wires one in without that wiring showing up here as a failure.
        var services = new ServiceCollection();
        services.AddWorkflowServices();

        var composed = services
            .Select(descriptor => descriptor.ServiceType)
            .Concat(services.Select(descriptor => descriptor.ImplementationType))
            .Where(type => type is not null)
            .Select(type => type!.FullName ?? type.Name)
            .ToArray();

        Assert.NotEmpty(composed);
        Assert.DoesNotContain(composed, name => name!.Contains("ReviewerIdentity", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProductionSourceFileReferencesTheResolverOutsideItsOwnTypes()
    {
        // Belt to the composition root's braces: a production file that names the namespace, the resolver,
        // the reader or the resolution type would be a place a dispatch path could grow. The resolver's own
        // files and the reader are the only permitted references, and the reader is the one that names them.
        var references = FindProductionReferences();

        Assert.Empty(references);
    }

    /// <summary>
    /// Every production source file that names a diagnostic-resolver type, minus the files that define them.
    /// </summary>
    private static IReadOnlyList<string> FindProductionReferences()
    {
        var defining = new HashSet<string>(StringComparer.Ordinal)
        {
            "GatewayRouteIdentityResolver.cs",
            "GatewayRouteObservation.cs",
            "GatewayRouteObservationChunk.cs",
            "GatewayRouteCandidate.cs",
            "GatewayRouteResolution.cs",
            "GatewayNativeIdentityDiagnostic.cs",
            "IGatewayRouteCandidateReader.cs",
            "SqliteGatewayRouteCandidateReader.cs"
        };

        var needles = new[]
        {
            "LLMWorkGUI.Application.ReviewerIdentity",
            "GatewayRouteIdentityResolver",
            "IGatewayRouteCandidateReader",
            "GatewayRouteResolution",
            "GatewayRouteCandidateSnapshot",
            "SqliteGatewayRouteCandidateReader"
        };

        var sources = EnumerateProductionSources().ToArray();

        // A scan that found nothing would pass without proving anything, so the enumeration itself is
        // asserted before the emptiness of its result is.
        Assert.NotEmpty(sources);
        Assert.Contains(sources, path => defining.Contains(Path.GetFileName(path)));

        var found = new List<string>();

        foreach (var file in sources)
        {
            if (defining.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var content = File.ReadAllText(file);

            if (needles.Any(needle => content.Contains(needle, StringComparison.Ordinal)))
            {
                found.Add(Path.GetRelativePath(FindRepositoryRoot(), file));
            }
        }

        return found;
    }

    private static IEnumerable<string> EnumerateProductionSources()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src");

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // The obj and bin directories hold generated copies of these very files, and a generated
            // assembly-informer or a cached project.assets.json would otherwise turn this scan into a
            // statement about build output rather than about production source.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }

    /// <summary>The route store, answered from memory. It resolves ids and nothing else.</summary>
    private sealed class StubRouteRepository : IRouteRepository
    {
        private readonly WorkflowRouteAssignment _assignment;

        public StubRouteRepository(WorkflowRouteAssignment assignment) => _assignment = assignment;

        public Task<WorkflowRouteAssignment?> GetAssignmentAsync(
            string routeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkflowRouteAssignment?>(
                string.Equals(routeId, _assignment.Route.Id, StringComparison.Ordinal) ? _assignment : null);
    }
}
