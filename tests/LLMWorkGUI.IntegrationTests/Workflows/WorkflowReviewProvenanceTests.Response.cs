using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData("Authorization: Bearer unsafe-native-token-123456789")]
    [InlineData("native\nsession")]
    public async Task UnsafeNativeSessionMetadataIsNotRetained(string nativeId)
    {
        var channel = new DispatchSafetyChannel { NativeSessionId = nativeId, Response = new WorkflowModelResponse("Answer") };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        var binding = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        Assert.Null((await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(binding.SessionId))!.NativeSessionId);
        Assert.Null((await provider.GetRequiredService<IWorkflowReviewResponseRepository>().GetByExecutionIdAsync(binding.ExecutionId))!.NativeSessionId);
    }
    [Fact]
    public async Task ParsedStoredResponseStillCannotReplaceMissingNativeProof()
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        var artifact = await CurrentArtifactAsync();
        channel.Response = new WorkflowModelResponse(System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, runId = RunId, stageId = StageId, reviewerRole = ReviewerRole,
            artifactId = artifact.ArtifactId, artifactSha256 = artifact.HashSha256, verdict = "Approve", summary = "Review findings"
        }));
        await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        var evidence = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        var stored = await provider.GetRequiredService<IWorkflowReviewResponseRepository>().GetByExecutionIdAsync(evidence.ExecutionId);
        Assert.Equal(WorkflowReviewVerdict.Approve, stored!.Parsed!.Verdict);
        var candidate = new LLMWorkGUI.Domain.ValueObjects.ReviewerVerdictRecord(ReviewerRole, RealRouteId, artifact.HashSha256,
            stored.Parsed.Verdict, stored.Parsed.Summary, AssignedAt, evidence.ExecutionId, StageId, artifact.ArtifactId);
        await Assert.ThrowsAsync<LLMWorkGUI.Application.Workflows.WorkflowValidationException>(() =>
            provider.GetRequiredService<IWorkflowRunService>().RecordReviewerVerdictAsync(RunId, candidate));
        Assert.Empty((await CurrentRunAsync()).Verdicts);
    }
    [Theory]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Ambiguous)]
    public async Task ModelResponseIsHashBoundAndSurvivesRepositoryReopenWithoutGrantingVerdict(ExecutionState state)
    {
        var response = new WorkflowModelResponse("Review answer. Authorization: Bearer secret-review-token-123456789");
        var channel = new DispatchSafetyChannel { Response = response, NativeSessionId = "native-review-1", Outcome = state };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        var evidence = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        var stored = await new SqliteWorkflowReviewResponseRepository(_database.Factory).GetByExecutionIdAsync(evidence.ExecutionId);
        Assert.NotNull(stored);
        Assert.Equal("native-review-1", stored.NativeSessionId);
        Assert.Equal(response.Sha256, stored.ReceivedSha256);
        Assert.DoesNotContain("secret-review-token", stored.Response.Content);
        Assert.True(stored.WasRedacted);
        Assert.Equal(new WorkflowModelResponse(stored.Response.Content).Sha256, stored.Response.Sha256);
        Assert.Equal("native-review-1", (await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(evidence.SessionId))!.NativeSessionId);
        Assert.Equal(state, evidence.ExecutionState);
        Assert.Empty((await CurrentRunAsync()).Verdicts);
        Assert.Equal(1, channel.Calls);
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WorkflowReviewResponses SET ResponseText='Approve' WHERE ExecutionId=$id";
        command.Parameters.AddWithValue("$id", evidence.ExecutionId);
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ResponseInsertFailureRollsBackSessionExecutionAndObservation()
    {
        var channel = new DispatchSafetyChannel { Response = new WorkflowModelResponse("Answer"), NativeSessionId = "native-answer" };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailResponseInsert BEFORE INSERT ON WorkflowReviewResponses BEGIN SELECT RAISE(ABORT,'injected response fault'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId));
        var evidence = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        Assert.Equal(ExecutionState.Queued, evidence.ExecutionState);
        Assert.Null(evidence.ObservedRouteId);
        Assert.Null(await provider.GetRequiredService<IWorkflowReviewResponseRepository>().GetByExecutionIdAsync(evidence.ExecutionId));
        var session = await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(evidence.SessionId);
        Assert.Equal(SessionState.Starting, session!.State);
        Assert.Equal(evidence.ExecutionId, session.ActiveExecutionId);
        Assert.Null(session.NativeSessionId);
        Assert.Equal(1, channel.Calls);
        Assert.True((await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Equal(1, channel.Calls);
    }

    [Fact]
    public async Task ResponseReadDetectsTamperedContentEvenIfSqlTriggerWasBypassed()
    {
        var channel = new DispatchSafetyChannel { Response = new WorkflowModelResponse("Original answer") };
        using var provider = CreateProvider(services => services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog([channel])));
        await StartRunAsync(provider, RealRouteId);
        await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        var evidence = Assert.Single(await provider.GetRequiredService<IWorkflowReviewEvidenceRepository>().ListByRunIdAsync(RunId));
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER TR_WorkflowReviewResponses_Immutable; UPDATE WorkflowReviewResponses SET ResponseText='Changed content'";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetRequiredService<IWorkflowReviewResponseRepository>()
            .GetByExecutionIdAsync(evidence.ExecutionId));
    }
}
