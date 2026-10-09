using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

// Uses existing product-command class real SQLite host/helpers. The single
// decorated port pauses return of a real refused result, never fabricates native dispatch.
public sealed partial class WorkflowRunProductCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnedAssignedReviewRemainsBoundToItsActuallyObservedRun(bool replaceRun)
    {
        using var host = await CreateInitializedHostAsync();
        await AssignArtifactTemplateAsync(host.Services, ArtifactKind,
            requiredReviewerRoles: new[] { "Reviewer" },
            roleBindings: new[] { new RoleBindingDefinition("Reviewer", "route-opencode", modelId: "model-1") });
        var services = host.Services;
        var heldReview = new HeldActualReviewResult(services.GetRequiredService<IWorkflowReviewRequestService>());
        var vm = new WorkflowLibraryViewModel(
            services.GetRequiredService<IWorkflowPackageRepository>(),
            services.GetRequiredService<IWorkflowVersionRepository>(),
            services.GetRequiredService<IWorkflowBindingRepository>(),
            services.GetRequiredService<IWorkflowBindingService>(),
            projectRepository: services.GetRequiredService<IProjectRepository>(),
            runRepository: services.GetRequiredService<IWorkflowRunRepository>(),
            runService: services.GetRequiredService<IWorkflowRunService>(),
            sessionRepository: services.GetRequiredService<ISessionRepository>(),
            executionRepository: services.GetRequiredService<IExecutionRepository>(),
            artifactBlobStore: services.GetRequiredService<IWorkflowArtifactBlobStore>(),
            reviewRequestService: heldReview);
        await vm.RefreshAsync();
        await vm.StartAssignedRunAsync();
        var originalRunId = vm.ObservedRun!.Id;
        var projectId = vm.SelectedProject!.Id;
        var packageId = vm.SelectedPackage!.Id;
        var versionId = vm.SelectedVersion!.Id;
        var publications = new List<(string ResultRun, string? ObservedRun)>();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(vm.AssignedReviewResult) && vm.AssignedReviewResult is { } published)
                publications.Add((published.RunId, vm.ObservedRun?.Id));
        };
        vm.StageArtifactPath = WriteArtifactFile("review-result-r2.md", Encoding.UTF8.GetBytes("owned artifact"));
        await vm.AttachStageArtifactAsync();
        Assert.True(vm.CanRequestAssignedReview);

        var request = vm.RequestAssignedReviewAsync();
        try
        {
            await heldReview.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(heldReview.Result!.IsRefusal); // Actual unavailable shipped channel.
            Assert.Equal(originalRunId, heldReview.Result.RunId);
            if (replaceRun)
            {
                var runService = services.GetRequiredService<IWorkflowRunService>();
                await runService.CancelRunAsync(originalRunId, "owned fixture replaces inactive review target");
                var replacement = await runService.StartRunAsync(projectId, packageId, versionId);
                await vm.ObserveActiveRunAsync();
                Assert.Equal(replacement.Id, vm.ObservedRun!.Id);
                Assert.NotEqual(originalRunId, vm.ObservedRun.Id);
                Assert.False(vm.HasAssignedReviewResult);
                Assert.Equal(projectId, vm.SelectedProject!.Id);
                Assert.Equal(packageId, vm.SelectedPackage!.Id);
                Assert.Equal(versionId, vm.SelectedVersion!.Id);
            }
        }
        finally { heldReview.Release.TrySetResult(); }
        await request;

        if (replaceRun)
        {
            Assert.NotEqual(originalRunId, vm.ObservedRun!.Id);
            Assert.False(vm.HasAssignedReviewResult);
            Assert.Null(vm.AssignedReviewResult);
        }
        else
        {
            Assert.Equal(originalRunId, vm.ObservedRun!.Id);
            Assert.Equal(originalRunId, vm.AssignedReviewResult!.RunId);
        }
        Assert.Contains(originalRunId, vm.RunCommandNotice, StringComparison.Ordinal);
        Assert.Equal(1, heldReview.Calls);
        // The final observation can clear an incorrectly attached result. Every public notification
        // must already be truthful, including the period before that subsequent database read settles.
        Assert.All(publications, publication => Assert.Equal(publication.ObservedRun, publication.ResultRun));
        if (!replaceRun)
            Assert.Contains(publications, publication => publication.ResultRun == originalRunId
                && publication.ObservedRun == originalRunId);
    }

    private sealed class HeldActualReviewResult(IWorkflowReviewRequestService inner) : IWorkflowReviewRequestService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WorkflowReviewRequestResult? Result { get; private set; }
        internal int Calls { get; private set; }
        public async Task<WorkflowReviewRequestResult> RequestAssignedReviewAsync(string runId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Result = await inner.RequestAssignedReviewAsync(runId, cancellationToken);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Result;
        }
    }
}
