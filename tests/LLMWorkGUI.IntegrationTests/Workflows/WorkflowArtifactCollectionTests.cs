using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowArtifactCollectionTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private ServiceProvider? _provider;
    private const string Kind = "ValidationReport";
    private const string ExecutionId = "collection-source";
    private static WorkflowNodeDefinition Node(string contract = Kind) => new("collect", WorkflowNodeKind.ArtifactCollection,
        "Collect", "Collector", successTargetNodeId: "done", artifactContract: contract,
        gateMetadata: new WorkflowNodeGateMetadata(WorkflowStageKind.Custom, [], false, Kind));

    private async Task<WorkflowRun> SetUpAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SqlAsync("""
            INSERT INTO WorkflowPackages (Id,Name,SourceType,OriginalHash,OriginalBlobId,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('p','p','Imported','h','b','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO WorkflowVersions (Id,WorkflowPackageId,VersionNumber,BlobId,OriginalHash,SourceType,CreatedAtUtc)
            VALUES ('v','p',1,'b','h','Imported','2026-10-01T00:00:00Z');
            """);
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);
        services.AddWorkflowServices();
        _provider = services.BuildServiceProvider();
        var store = _provider!.GetRequiredService<IWorkflowTemplateStore>();
        await store.SaveAsync(new WorkflowTemplateDefinition("collect-template", 1, "Collection", "Real durable collection",
            new WorkflowGraph("collect", [Node(), new WorkflowNodeDefinition("done", WorkflowNodeKind.TerminalOutcome, "Done", "Collector")]),
            [new RoleBindingDefinition("Collector", "route-1")], [], false, DateTimeOffset.UtcNow));
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment("assignment", "project-1", "collect-template", 1, DateTimeOffset.UtcNow));
        var run = await _provider!.GetRequiredService<IWorkflowRunService>().StartRunAsync("project-1", "p", "v");
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync(executionId: ExecutionId, state: "Succeeded", endedAt: DateTimeOffset.UtcNow);
        await SqlAsync("UPDATE Sessions SET WorkflowRunId=$run WHERE Id='session-1';", run.Id);
        await SqlAsync("UPDATE Executions SET CreatedAtUtc=(SELECT StartedAtUtc FROM WorkflowRuns WHERE Id=$run), StartedAtUtc=(SELECT StartedAtUtc FROM WorkflowRuns WHERE Id=$run)", run.Id);
        return run;
    }

    private WorkflowNodeExecutionRequest Request(WorkflowRun run, Stream content, WorkflowNodeDefinition? node = null,
        string? executionId = ExecutionId, string? projectId = null) => new(node ?? Node(), new RoleBindingDefinition("Collector", "route-1"),
            projectId ?? run.ProjectId, _database.GetWorkspacePath(), executionId: executionId,
            artifactCollection: new WorkflowArtifactCollectionInput(run.Id, content, DataClassification.PrivateSource));

    [Fact]
    public async Task ProductCollectorPersistsVerifiedBytesAndExecutionLinkBeforeSuccessAndSurvivesReopen()
    {
        var run = await SetUpAsync();
        await SqlAsync("UPDATE Executions SET ObservedRouteId='route-1' WHERE Id='collection-source'");
        var sourceBefore = (await _provider!.GetRequiredService<IExecutionRepository>().GetByIdAsync(ExecutionId))!;
        using var content = Content();
        var result = await _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(Request(run, content));
        Assert.True(result.IsSuccess);
        Assert.True(result.IsExistingExecutionPostprocessing);
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal("done", result.NextNodeId);
        var artifact = Assert.Single(result.CollectedArtifacts);
        Assert.Equal(ExecutionId, artifact.ExecutionId);
        Assert.Equal(run.Id, artifact.RunId);
        Assert.Equal("collect", artifact.StageId);
        await using var verified = await _provider!.GetRequiredService<IWorkflowArtifactBlobStore>().OpenVerifiedAsync(artifact.BlobId, 1024);
        Assert.NotNull(verified);
        using var reader = new StreamReader(verified!, leaveOpen: true);
        Assert.Equal("real artifact bytes", await reader.ReadToEndAsync());
        TestSqlitePool.Clear(_database.Factory);
        var reopened = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal(artifact.ArtifactId, Assert.Single(reopened!.Artifacts).ArtifactId);
        Assert.Equal(ExecutionId, reopened.Artifacts[0].ExecutionId);
        var executionRepository = _provider!.GetRequiredService<IExecutionRepository>();
        var sourceAfter = (await executionRepository.GetByIdAsync(ExecutionId))!;
        Assert.Equal((sourceBefore.SessionId, sourceBefore.ClientRequestId, sourceBefore.State, sourceBefore.FailureReason,
            sourceBefore.RequestedRouteId, sourceBefore.ObservedRouteId, sourceBefore.CreatedAt, sourceBefore.StartedAt, sourceBefore.EndedAt),
            (sourceAfter.SessionId, sourceAfter.ClientRequestId, sourceAfter.State, sourceAfter.FailureReason,
            sourceAfter.RequestedRouteId, sourceAfter.ObservedRouteId, sourceAfter.CreatedAt, sourceAfter.StartedAt, sourceAfter.EndedAt));
        Assert.Contains(artifact.ArtifactId, sourceAfter.Artifacts);
        Assert.Contains(artifact.ArtifactId, Assert.Single(await executionRepository.ListBySessionAsync("session-1")).Artifacts);
        var advanced = await _provider!.GetRequiredService<IWorkflowRunService>().AdvanceStageAsync(run.Id, "stored artifact verified");
        Assert.Equal("done", advanced.CurrentStageId);
        Assert.Equal(0, await _database.CountAsync("ExecutionEvents")); // collection does not invent a native response
    }

    [Theory]
    [InlineData("missing-execution")]
    [InlineData("foreign-project")]
    [InlineData("forged-contract")]
    [InlineData("legacy-attach")]
    [InlineData("foreign-run")]
    [InlineData("ambiguous")]
    public async Task CollectionRefusesUnboundOrForgedTargets(string fault)
    {
        var run = await SetUpAsync();
        using var content = Content();
        if (fault == "foreign-run") await SqlAsync("UPDATE Sessions SET WorkflowRunId='other-run'");
        if (fault == "ambiguous") await _database.UpdateExecutionStateAsync(ExecutionId, "Ambiguous");
        if (fault == "legacy-attach")
            await Assert.ThrowsAsync<WorkflowValidationException>(() => _provider!.GetRequiredService<IWorkflowRunService>()
                .RecordStageArtifactAsync(run.Id, "collect", Kind, content, DataClassification.PrivateSource));
        else
            await Assert.ThrowsAsync<WorkflowValidationException>(() => _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(
                Request(run, content, fault == "forged-contract" ? Node("other-kind") : null,
                    fault == "missing-execution" ? null : ExecutionId, fault == "foreign-project" ? "foreign" : null)));
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("late-ownership")]
    [InlineData("late-terminal")]
    [InlineData("cancel")]
    public async Task StorageFailureOrLateTargetChangeNeverReturnsSuccess(string fault)
    {
        var run = await SetUpAsync();
        using var cancellation = new CancellationTokenSource();
        if (fault == "storage") await SqlAsync("CREATE TRIGGER RejectCollection BEFORE INSERT ON Artifacts BEGIN SELECT RAISE(ABORT,'injected storage failure'); END;");
        using var content = new HookStream(async () =>
        {
            if (fault == "late-ownership") await SqlAsync("UPDATE Sessions SET WorkflowRunId='other-run'");
            if (fault == "late-terminal") await SqlAsync("UPDATE WorkflowRuns SET State='Cancelled', TerminalOutcome='Cancelled', EndedAtUtc=StartedAtUtc WHERE Id=$run", run.Id);
            if (fault == "cancel") cancellation.Cancel();
        });
        var error = await Record.ExceptionAsync(() => _provider!.GetRequiredService<IWorkflowNodeExecutor>()
            .ExecuteAsync(Request(run, content), cancellation.Token));
        Assert.NotNull(error);
        if (fault == "storage") Assert.IsType<SqliteException>(error);
        else if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<WorkflowValidationException>(error);
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
        Assert.Equal(1, await _database.CountAsync("Executions", "State='Succeeded'"));
    }

    [Fact]
    public async Task OversizedContentIsRefusedBeforeBlobOrArtifactCommit()
    {
        var run = await SetUpAsync();
        using var content = new MemoryStream(new byte[WorkflowRunService.MaxExecutionArtifactBytes + 1]);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(Request(run, content)));
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
    }

    [Fact]
    public async Task LostExecutionAuthorityBlocksStageAdvanceAfterSuccessfulCollection()
    {
        var run = await SetUpAsync();
        using var content = Content();
        await _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(Request(run, content));
        await _database.UpdateExecutionStateAsync(ExecutionId, "Ambiguous");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _provider!.GetRequiredService<IWorkflowRunService>().AdvanceStageAsync(run.Id, "attempt"));
        Assert.Equal("collect", (await _provider!.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(run.Id))!.CurrentStageId);
    }

    private static MemoryStream Content() => new(Encoding.UTF8.GetBytes("real artifact bytes"), false);

    [Fact]
    public async Task EmptyExecutionArtifactsReadAsEmptyOnBundledSqlite()
    {
        await SetUpAsync();
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version() || '|' || (SELECT json_group_array(Id ORDER BY CreatedAtUtc,Id) FROM Artifacts WHERE 0)";
        Assert.Equal("3.53.3|[]", await command.ExecuteScalarAsync());
        var repository = _provider!.GetRequiredService<IExecutionRepository>();
        Assert.Empty((await repository.GetByIdAsync(ExecutionId))!.Artifacts);
        Assert.Empty(Assert.Single(await repository.ListBySessionAsync("session-1")).Artifacts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityChangedAfterBlobCheckCannotCommitStageTransition(bool terminalRun)
    {
        var run = await SetUpAsync();
        using var content = Content();
        await _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(Request(run, content));
        var blobs = new VerifyHookBlobStore(_provider!.GetRequiredService<IWorkflowArtifactBlobStore>(), async () =>
        {
            if (terminalRun) await SqlAsync("UPDATE WorkflowRuns SET State='Cancelled', TerminalOutcome='Cancelled', EndedAtUtc=StartedAtUtc WHERE Id=$run", run.Id);
            else await _database.UpdateExecutionStateAsync(ExecutionId, "Ambiguous");
        });
        var service = new WorkflowRunService(_provider!.GetRequiredService<IWorkflowRunRepository>(),
            _provider!.GetRequiredService<WorkflowScheme>(), _provider!.GetRequiredService<IWorkflowTemplateStore>(),
            blobs, _provider!.GetRequiredService<IUserApprovalIdentity>());
        await Assert.ThrowsAsync<WorkflowValidationException>(() => service.AdvanceStageAsync(run.Id, "attempt"));
        Assert.Equal("collect", (await _provider!.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(run.Id))!.CurrentStageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewArtifactOrChangedRunPayloadPreventsStaleTransitionOverwrite(bool newArtifact)
    {
        var run = await SetUpAsync();
        using var content = Content();
        await _provider!.GetRequiredService<IWorkflowNodeExecutor>().ExecuteAsync(Request(run, content));
        var blobs = new VerifyHookBlobStore(_provider!.GetRequiredService<IWorkflowArtifactBlobStore>(), () => SqlAsync(newArtifact
            ? """
                INSERT INTO Artifacts (Id,WorkflowRunId,ExecutionId,StageId,Kind,BlobId,HashSha256,SizeBytes,DataClassification,CreatedAtUtc)
                SELECT 'new-before-transition',WorkflowRunId,ExecutionId,StageId,Kind,BlobId,HashSha256,SizeBytes,DataClassification,CreatedAtUtc
                FROM Artifacts WHERE WorkflowRunId=$run LIMIT 1;
                """
            : "UPDATE WorkflowRuns SET EvidenceRedactedJson=json_set(EvidenceRedactedJson,'$.currentRole','changed-role') WHERE Id=$run", run.Id));
        var service = new WorkflowRunService(_provider!.GetRequiredService<IWorkflowRunRepository>(),
            _provider!.GetRequiredService<WorkflowScheme>(), _provider!.GetRequiredService<IWorkflowTemplateStore>(),
            blobs, _provider!.GetRequiredService<IUserApprovalIdentity>());
        await Assert.ThrowsAsync<WorkflowValidationException>(() => service.AdvanceStageAsync(run.Id, "attempt"));
        var stored = (await _provider!.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(run.Id))!;
        Assert.Equal("collect", stored.CurrentStageId);
        if (newArtifact) Assert.Equal(2, stored.Artifacts.Count);
        else Assert.Equal("changed-role", stored.CurrentRole);
    }

    private sealed class VerifyHookBlobStore(IWorkflowArtifactBlobStore inner, Func<Task> hook) : IWorkflowArtifactBlobStore
    {
        public Task<WorkflowArtifactBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default) => inner.SaveAsync(content, cancellationToken);
        public Task<Stream?> OpenVerifiedAsync(string blobId, long maxBytes, CancellationToken cancellationToken = default) => inner.OpenVerifiedAsync(blobId, maxBytes, cancellationToken);
        public async Task<bool> VerifyAsync(string blobId, CancellationToken cancellationToken = default)
        {
            var result = await inner.VerifyAsync(blobId, cancellationToken);
            await hook();
            return result;
        }
    }
    private sealed class HookStream(Func<Task> hook) : MemoryStream(Encoding.UTF8.GetBytes("real artifact bytes"), false)
    {
        private bool _called;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_called) { _called = true; await hook(); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
    private async Task SqlAsync(string sql, string? runId = null)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
    public void Dispose() { _provider?.Dispose(); _database.Dispose(); }
}
