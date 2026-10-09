using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Historical/domain fixture setup only. Every seeded claim first proves that the product refuses it.
/// An aggregate traversal proves graph invariants, never model review or production gate acceptance.
/// </summary>
internal static class HistoricalWorkflowReviewFixture
{
    public static async Task SeedAsync(IServiceProvider services, string runId, ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default)
    {
        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            services.GetRequiredService<IWorkflowRunService>().RecordReviewerVerdictAsync(runId, verdict, cancellationToken));
        if (!refusal.Message.Contains("model response", StringComparison.Ordinal)) throw refusal;
        var repository = services.GetRequiredService<IWorkflowRunRepository>();
        var run = (await repository.GetByIdAsync(runId, cancellationToken))!;
        run.RecordReviewerVerdict(verdict);
        await repository.SaveAsync(run, cancellationToken);
    }

    public static async Task<WorkflowRun> AdvanceAggregateAsync(IServiceProvider services, string runId, string reason)
    {
        var service = services.GetRequiredService<IWorkflowRunService>();
        var repository = services.GetRequiredService<IWorkflowRunRepository>();
        var run = (await repository.GetByIdAsync(runId))!;
        var scheme = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, runId).Scheme;
        var stage = scheme.GetRequiredStage(run.CurrentStageId);
        if (stage.RequiredReviewerRoles.Count == 0) return await service.AdvanceStageAsync(runId, reason);
        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(() => service.AdvanceStageAsync(runId, reason));
        Assert.Contains("model response", refusal.Message, StringComparison.Ordinal);
        var evidence = await services.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(runId);
        run.AdvanceTo(stage, scheme.GetRequiredStage(stage.NextStageId!), reason,
            (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(), evidence);
        await repository.SaveAsync(run);
        return run;
    }
}

