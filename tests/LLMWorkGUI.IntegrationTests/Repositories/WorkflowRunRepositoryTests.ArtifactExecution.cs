using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed partial class WorkflowRunRepositoryTests
{
    private async Task<(SqliteWorkflowRunRepository Repository, WorkflowRun Run)> SeedArtifactExecutionAsync()
    {
        await InitializeRunDependenciesAsync();
        await _database.SeedExecutionAsync(executionId: "artifact-execution", state: "Succeeded",
            endedAt: Timestamp.AddMinutes(1));
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = WorkflowRun.Start(RunId, ProjectId, PackageId, VersionId, "session-1",
            CreateStage("stage-1", nextStageId: "stage-2"), Timestamp);
        await repository.SaveAsync(run);
        await ArtifactSqlAsync("UPDATE Sessions SET WorkflowRunId=$run WHERE Id='session-1';");
        await ArtifactSqlAsync("UPDATE Executions SET CreatedAtUtc=(SELECT StartedAtUtc FROM WorkflowRuns WHERE Id=$run), StartedAtUtc=(SELECT StartedAtUtc FROM WorkflowRuns WHERE Id=$run);");
        return (repository, run);
    }

    private static WorkflowArtifactEvidence LinkedArtifact() => new("linked-artifact", RunId, "stage-1",
        "DocumentBundle", HashA, HashA, Timestamp.AddMinutes(2), 12, DataClassification.PrivateSource,
        executionId: "artifact-execution");

    [Fact]
    public async Task ArtifactExecutionAssociationSurvivesReopen()
    {
        var (repository, run) = await SeedArtifactExecutionAsync();
        var artifact = LinkedArtifact();
        await repository.SaveArtifactAsync(run.WithArtifact(artifact), artifact);
        TestSqlitePool.Clear(_database.Factory);
        var reopened = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal("artifact-execution", Assert.Single(reopened!.Artifacts).ExecutionId);
    }

    [Theory]
    [InlineData("UPDATE Sessions SET WorkflowRunId=NULL WHERE Id='session-1'")]
    [InlineData("UPDATE Sessions SET WorkflowRunId='foreign-run' WHERE Id='session-1'")]
    [InlineData("UPDATE Executions SET State='Ambiguous' WHERE Id='artifact-execution'")]
    [InlineData("UPDATE Executions SET EndedAtUtc=NULL WHERE Id='artifact-execution'")]
    [InlineData("DELETE FROM Executions WHERE Id='artifact-execution'")]
    [InlineData("UPDATE Executions SET CreatedAtUtc='2020-01-01T00:00:00Z' WHERE Id='artifact-execution'")]
    [InlineData("UPDATE Executions SET EndedAtUtc='not-a-date' WHERE Id='artifact-execution'")]
    [InlineData("INSERT INTO Projects (Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc) SELECT 'foreign-project',DisplayName,RootPath || '-foreign',DataClassification,CreatedAtUtc,UpdatedAtUtc FROM Projects WHERE Id='project-1'; UPDATE Sessions SET ProjectId='foreign-project'")]
    public async Task InvalidArtifactExecutionRefusesWholeWrite(string mutation)
    {
        var (repository, run) = await SeedArtifactExecutionAsync();
        await ArtifactSqlAsync(mutation);
        var artifact = LinkedArtifact();
        await Assert.ThrowsAsync<WorkflowValidationException>(() => repository.SaveArtifactAsync(run.WithArtifact(artifact), artifact));
        Assert.Empty((await repository.GetByIdAsync(run.Id))!.Artifacts);
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
    }

    [Fact]
    public async Task PersistedArtifactExecutionCannotBeReboundOrErased()
    {
        var (repository, run) = await SeedArtifactExecutionAsync();
        var artifact = LinkedArtifact();
        await repository.SaveArtifactAsync(run.WithArtifact(artifact), artifact);
        await Assert.ThrowsAsync<SqliteException>(() => ArtifactSqlAsync("UPDATE Artifacts SET ExecutionId=NULL WHERE Id='linked-artifact'"));
    }

    [Fact]
    public async Task SqlTriggerRejectsUnownedExecutionWithoutRepositoryValidation()
    {
        await SeedArtifactExecutionAsync();
        await ArtifactSqlAsync("UPDATE Sessions SET WorkflowRunId='foreign-run'");
        await Assert.ThrowsAsync<SqliteException>(() => ArtifactSqlAsync($"""
            INSERT INTO Artifacts (Id,WorkflowRunId,ExecutionId,StageId,Kind,BlobId,HashSha256,SizeBytes,DataClassification,CreatedAtUtc)
            VALUES ('raw-artifact',$run,'artifact-execution','stage-1','DocumentBundle','{HashA}','{HashA}',12,'PrivateSource','2026-10-05T00:00:00Z');
            """));
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
    }

    private async Task ArtifactSqlAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run", RunId);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("UPDATE Executions SET StartedAtUtc=NULL")]
    [InlineData("UPDATE Executions SET StartedAtUtc='not-a-date'")]
    [InlineData("UPDATE Executions SET StartedAtUtc='2020-01-01T00:00:00Z'")]
    [InlineData("UPDATE Executions SET StartedAtUtc='2099-01-01T00:00:00Z'")]
    public async Task InvalidStartChronologyIsRefusedBeforeContentCapture(string mutation)
    {
        var (repository, run) = await SeedArtifactExecutionAsync();
        await ArtifactSqlAsync(mutation);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => repository.ValidateArtifactExecutionAsync(run, "artifact-execution"));
    }

    [Theory]
    [InlineData("UPDATE Executions SET StartedAtUtc=NULL")]
    [InlineData("UPDATE Executions SET StartedAtUtc='not-a-date'")]
    [InlineData("UPDATE Executions SET StartedAtUtc='2020-01-01T00:00:00Z'")]
    [InlineData("UPDATE Executions SET StartedAtUtc='2099-01-01T00:00:00Z'")]
    [InlineData("UPDATE WorkflowRuns SET State='Cancelled'")]
    [InlineData("UPDATE WorkflowRuns SET EvidenceRedactedJson=json_set(EvidenceRedactedJson,'$.currentStageId','stage-2')")]
    [InlineData("UPDATE WorkflowRuns SET EvidenceRedactedJson='{}'")]
    public async Task SqlBackstopRejectsInvalidChronologyOrCurrentStage(string mutation)
    {
        await SeedArtifactExecutionAsync();
        await ArtifactSqlAsync(mutation);
        await Assert.ThrowsAsync<SqliteException>(() => ArtifactSqlAsync($"""
            INSERT INTO Artifacts (Id,WorkflowRunId,ExecutionId,StageId,Kind,BlobId,HashSha256,SizeBytes,DataClassification,CreatedAtUtc)
            VALUES ('raw-artifact',$run,'artifact-execution','stage-1','DocumentBundle','{HashA}','{HashA}',12,'PrivateSource','2026-10-05T00:00:00Z');
            """));
        Assert.Equal(0, await _database.CountAsync("Artifacts"));
    }

    [Fact]
    public async Task ExecutionArtifactsIncludeBothExecutionAndRunScopedAssociations()
    {
        var (repository, run) = await SeedArtifactExecutionAsync();
        var artifact = LinkedArtifact();
        await repository.SaveArtifactAsync(run.WithArtifact(artifact), artifact);
        await ArtifactSqlAsync($"""
            INSERT INTO Artifacts (Id,ExecutionId,Kind,BlobId,HashSha256,SizeBytes,DataClassification,CreatedAtUtc)
            VALUES ('execution-only','artifact-execution','Output','{HashA}','{HashA}',12,'PrivateSource','2026-10-05T00:00:00Z');
            """);
        var executions = new SqliteExecutionRepository(_database.Factory, new LLMWorkGUI.Infrastructure.Security.SensitiveDataFilter());
        var execution = await executions.GetByIdAsync("artifact-execution");
        Assert.Equal(new[] { "execution-only", "linked-artifact" }, execution!.Artifacts.Order().ToArray());
        Assert.Equal(execution.Artifacts, Assert.Single(await executions.ListBySessionAsync("session-1")).Artifacts);
        Assert.Equal("linked-artifact", Assert.Single((await repository.GetByIdAsync(run.Id))!.Artifacts).ArtifactId);
    }

    [Fact]
    public async Task ArtifactInsertTransactionAlreadyExcludesCompetingRunWriter()
    {
        var (_, run) = await SeedArtifactExecutionAsync();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var writer = new SqliteWorkflowRunRepository(new HeldArtifactInsertFactory(_database.Factory, entered, resume));
        var artifact = LinkedArtifact();
        var saving = Task.Factory.StartNew(() => writer.SaveArtifactAsync(run.WithArtifact(artifact), artifact),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await using var connection = await _database.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "UPDATE WorkflowRuns SET State='Cancelled' WHERE Id=$run";
            command.Parameters.AddWithValue("$run", RunId);
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });
        }
        finally { resume.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(10)); }
        var restored = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(RunId);
        Assert.Equal(run.State, restored!.State);
        Assert.Equal("artifact-execution", Assert.Single(restored.Artifacts).ExecutionId);
    }

    private sealed class HeldArtifactInsertFactory(ISqliteConnectionFactory inner, ManualResetEventSlim entered,
        ManualResetEventSlim resume) : ISqliteConnectionFactory
    {
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => new SqliteConnectionStringBuilder(inner.ConnectionString) { Pooling = false }.ToString();
        public SqliteConnection CreateConnection() => new(ConnectionString);
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            connection.CreateFunction("hold_collection_insert", () =>
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Collection barrier expired.");
                return 1;
            });
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER PauseCollection BEFORE INSERT ON Artifacts BEGIN SELECT hold_collection_insert(); END;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
    }
}
