using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData("stage")]
    [InlineData("terminal")]
    [InlineData("artifact")]
    public async Task TargetChangesAfterPreflight_AdmissionRefusesWithoutDispatchOrOrphanRows(string change)
    {
        var channel = new DispatchSafetyChannel();
        IWorkflowRunService? runs = null;
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel]));
            services.AddSingleton<IWorkflowReviewDispatchStore>(new BeforeAdmissionStore(
                new SqliteWorkflowReviewDispatchStore(_database.Factory), async () =>
                {
                    if (change == "artifact")
                    {
                        await runs!.RecordStageArtifactAsync(RunId, StageId, ArtifactKind,
                            new MemoryStream(Encoding.UTF8.GetBytes("new document after preflight")),
                            DataClassification.PrivateSource);
                        return;
                    }
                    await using var connection = await _database.Factory.OpenConnectionAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = change == "stage"
                        ? "UPDATE WorkflowRuns SET EvidenceRedactedJson=json_set(EvidenceRedactedJson,'$.currentStageId',$stage) WHERE Id=$run"
                        : "UPDATE WorkflowRuns SET State='Cancelled',TerminalOutcome='Cancelled',EndedAtUtc=$time WHERE Id=$run";
                    command.Parameters.AddWithValue("$run", RunId);
                    command.Parameters.AddWithValue("$stage", TerminalStageId);
                    command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                    Assert.Equal(1, await command.ExecuteNonQueryAsync());
                }));
        });
        runs = provider.GetRequiredService<IWorkflowRunService>();
        await StartRunAsync(provider, RealRouteId);
        var before = await ReviewerRowCountsAsync();

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);

        Assert.True(result.IsRefusal);
        Assert.Equal(0, channel.Calls);
        Assert.Equal(before, await ReviewerRowCountsAsync());
        Assert.Empty(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
    }

    private sealed class BeforeAdmissionStore(IWorkflowReviewDispatchStore inner, Func<Task> before)
        : IWorkflowReviewDispatchStore
    {
        public async Task<bool> TryAdmitAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.TryAdmitAsync(session, execution, evidence, cancellationToken);
        }

        public Task CompleteAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
            CancellationToken cancellationToken = default) => inner.CompleteAsync(session, execution, evidence, cancellationToken);
    }
}
