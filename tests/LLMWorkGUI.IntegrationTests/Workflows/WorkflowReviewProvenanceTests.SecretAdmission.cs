using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData("Authorization: Bearer synthetic-review-token-123456789", true)]
    [InlineData("api_key=synthetic-review-key-123456789", true)]
    [InlineData("Ordinary project requirements and public release notes.", false)]
    public async Task VerifiedReviewArtifactIsScannedBeforeAnyReviewerAdmission(string content, bool blocked)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await provider.GetRequiredService<IWorkflowRunService>().RecordStageArtifactAsync(RunId, StageId,
            ArtifactKind, new MemoryStream(Encoding.UTF8.GetBytes(content)), DataClassification.PrivateSource);
        var rowsBefore = await ReviewerRowCountsAsync();
        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        Assert.Equal(blocked, result.IsRefusal);
        Assert.Equal(blocked ? 0 : 1, channel.Calls);
        if (blocked)
        {
            Assert.Equal(rowsBefore, await ReviewerRowCountsAsync());
            Assert.DoesNotContain(content, Assert.Single(result.Roles).Refusal!);
        }
    }

    [Fact]
    public async Task MissingScannerCannotAdmitAReviewerTurn()
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel]));
            services.AddSingleton<IWorkflowSecretScanner>(_ => null!);
        });
        await StartRunAsync(provider, RealRouteId);
        var before = await ReviewerRowCountsAsync();
        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        Assert.True(result.IsRefusal);
        Assert.Equal(0, channel.Calls);
        Assert.Equal(before, await ReviewerRowCountsAsync());
    }

    [Fact]
    public async Task ScannerFailureLeavesNoPendingReviewerAndDoesNotDiscloseExceptionContent()
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel]));
            services.AddSingleton<IWorkflowSecretScanner>(new FailingReviewScanner());
        });
        await StartRunAsync(provider, RealRouteId);
        var before = await ReviewerRowCountsAsync();
        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        Assert.True(result.IsRefusal);
        Assert.Equal(0, channel.Calls);
        Assert.Equal(before, await ReviewerRowCountsAsync());
        Assert.DoesNotContain("synthetic-sensitive-exception-detail", Assert.Single(result.Roles).Refusal!);
    }

    private sealed class FailingReviewScanner : IWorkflowSecretScanner
    {
        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("synthetic-sensitive-exception-detail");
    }
}
