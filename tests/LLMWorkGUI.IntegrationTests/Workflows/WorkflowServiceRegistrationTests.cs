using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowServiceRegistrationTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void AddWorkflowServices_RegistersWorkflowContractsAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_directory.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var packageRepository = provider.GetRequiredService<IWorkflowPackageRepository>();
        var versionRepository = provider.GetRequiredService<IWorkflowVersionRepository>();

        Assert.Same(packageRepository, provider.GetRequiredService<IWorkflowPackageRepository>());
        Assert.Same(versionRepository, provider.GetRequiredService<IWorkflowVersionRepository>());

        Assert.IsType<SafeArchiveValidator>(provider.GetRequiredService<IWorkflowArchiveValidator>());
        Assert.IsType<WorkflowImportService>(provider.GetRequiredService<IWorkflowImportService>());
        Assert.IsType<WorkflowExportService>(provider.GetRequiredService<IWorkflowExportService>());
    }

    [Fact]
    public void AddWorkflowServices_RegistersPreviewManifestAndScratchServicesAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_directory.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var previewService = provider.GetRequiredService<IWorkflowPreviewService>();
        var manifestParser = provider.GetRequiredService<IWorkflowManifestParser>();
        var scratchManager = provider.GetRequiredService<IScratchWorkspaceManager>();

        Assert.IsType<WorkflowPreviewService>(previewService);
        Assert.IsType<WorkflowManifestParser>(manifestParser);
        Assert.IsType<ScratchWorkspaceManager>(scratchManager);

        Assert.Same(previewService, provider.GetRequiredService<IWorkflowPreviewService>());
        Assert.Same(manifestParser, provider.GetRequiredService<IWorkflowManifestParser>());
        Assert.Same(scratchManager, provider.GetRequiredService<IScratchWorkspaceManager>());
    }

    [Fact]
    public void AddWorkflowServices_ComposesTheRunServiceOntoTheDurableTemplateStore()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_directory.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var runService = provider.GetRequiredService<IWorkflowRunService>();

        Assert.IsType<WorkflowRunService>(runService);
        Assert.Same(runService, provider.GetRequiredService<IWorkflowRunService>());

        // The run service decides a run's assigned template identity, so the store it resolves has to be
        // the durable one the Studio writes to, not a private in-memory fallback.
        Assert.IsType<SqliteWorkflowTemplateStore>(provider.GetRequiredService<IWorkflowTemplateStore>());
        Assert.Same(
            provider.GetRequiredService<IWorkflowTemplateStore>(),
            provider.GetRequiredService<IWorkflowTemplateStore>());
        Assert.Same(
            provider.GetRequiredService<IWorkflowRunRepository>(),
            provider.GetRequiredService<IWorkflowRunRepository>());
        Assert.NotNull(provider.GetRequiredService<WorkflowScheme>());
    }

    [Fact]
    public void AddWorkflowServices_RegistersTheReadOnlyReviewerChannelAndItStillRefusesEveryRoute()
    {
        // The production composition, not a hand-built one: the real loopback client, the real route store
        // and the real catalog.
        var services = new ServiceCollection();
        services.AddInfrastructure(_directory.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var channel = Assert.Single(provider.GetServices<IWorkflowNodeChannel>());

        // A real channel on the real client. An empty catalog would hide a missing fact behind a missing
        // component, and a channel that echoed the requested route back would fabricate the observation
        // every downstream gate reads.
        var review = Assert.IsType<StarCliProxyReviewReadOnlyChannel>(channel);
        Assert.Equal(StarCliProxyReviewReadOnlyChannel.ChannelIdValue, review.ChannelId);
        Assert.Equal(
            new[] { WorkflowReviewRequestService.ReviewerCapability },
            review.Capabilities.ToArray());
        Assert.False(review.SupportsRoute("route-opencode"));
        Assert.False(review.SupportsRoute("route-real"));

        // The loopback client is composed beside it, and the shipped locator still has nowhere to send:
        // the channel is ready and the answer is still no.
        Assert.NotNull(provider.GetService<IStarCliProxyClient>());
        Assert.IsType<UnaddressableReviewChannelGatewayLocator>(
            provider.GetRequiredService<IReviewChannelGatewayLocator>());

        // So the catalog refuses the real route by name, with the channel's own reason attached rather than
        // a bare "nothing matched".
        var catalog = provider.GetRequiredService<IWorkflowChannelCatalog>();

        var refusal = Assert.Throws<WorkflowValidationException>(
            () => catalog.ResolveChannel("route-real", new[] { WorkflowReviewRequestService.ReviewerCapability }));

        Assert.Contains("No channel with proven capabilities", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Declined:", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            "resolved to one persisted Routes.Id",
            refusal.Message,
            StringComparison.Ordinal);
    }
}
