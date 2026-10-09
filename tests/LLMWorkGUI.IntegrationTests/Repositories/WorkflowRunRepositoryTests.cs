using System.Globalization;
using System.Text.Json;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed partial class WorkflowRunRepositoryTests : IDisposable
{
    [Theory]
    [InlineData("id")]
    [InlineData("project")]
    [InlineData("active")]
    [InlineData("legacy-control")]
    public async Task ConcurrentCommit_DoesNotMixRunAndArtifactSnapshots(string readKind)
    {
        await InitializeRunDependenciesAsync();
        var writer = new SqliteWorkflowRunRepository(_database.Factory);
        var initialStage = CreateStage("stage-1", nextStageId: "stage-2");
        var nextStage = CreateStage("stage-2");
        var run = WorkflowRun.Start(RunId, ProjectId, PackageId, VersionId, "session-1", initialStage, Timestamp);
        await writer.SaveAsync(run);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var factory = new PausedRunReadFactory(_database.Factory, entered, resume);
        var reader = new SqliteWorkflowRunRepository(factory);
        var reading = Task.Factory.StartNew(async () =>
        {
            if (readKind == "legacy-control")
            {
                // Negative control: the former two-statement read without a transaction really
                // observes the old run payload together with the newly committed artifact.
                await using var connection = await factory.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id='run-1'";
                var json = (string)(await command.ExecuteScalarAsync())!;
                command.CommandText = "SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId='run-1'";
                using var payload = JsonDocument.Parse(json);
                return (Stage: payload.RootElement.GetProperty("currentStageId").GetString(),
                    ArtifactCount: (int)(long)(await command.ExecuteScalarAsync())!);
            }
            var observed = readKind switch
            {
                "id" => await reader.GetByIdAsync(RunId),
                "active" => await reader.GetActiveByProjectIdAsync(ProjectId),
                _ => Assert.Single(await reader.GetByProjectIdAsync(ProjectId))
            };
            return (Stage: (string?)observed!.CurrentStageId, ArtifactCount: observed.Artifacts.Count);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The run read did not reach the snapshot barrier.");
            run.AdvanceTo(initialStage, nextStage, "Concurrent transition", Timestamp.AddMinutes(1));
            var artifact = new WorkflowArtifactEvidence("concurrent-artifact", RunId, "stage-2", "DocumentBundle",
                HashA, HashA, Timestamp.AddMinutes(2), 12, DataClassification.PrivateSource);
            await writer.SaveArtifactAsync(run.WithArtifact(artifact), artifact).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resume.Set();
            await reading.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var result = await reading;
        Assert.Equal("stage-1", result.Stage);
        Assert.Equal(readKind == "legacy-control" ? 1 : 0, result.ArtifactCount);
        var committed = await writer.GetByIdAsync(RunId);
        Assert.Equal("stage-2", committed!.CurrentStageId);
        Assert.Single(committed.Artifacts);
    }

    private sealed class PausedRunReadFactory(ISqliteConnectionFactory inner,
        ManualResetEventSlim entered, ManualResetEventSlim resume) : ISqliteConnectionFactory
    {
        public string DatabasePath => inner.DatabasePath;
        // TEMP views belong to a native connection; do not return this instrumented one to the pool.
        public string ConnectionString => new SqliteConnectionStringBuilder(inner.ConnectionString) { Pooling = false }.ToString();
        public SqliteConnection CreateConnection() => new(ConnectionString);
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            try
            {
                await connection.OpenAsync(cancellationToken);
                connection.CreateFunction("closure_read_barrier", () =>
                {
                    entered.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Snapshot barrier timed out.");
                    return 1;
                });
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TEMP VIEW WorkflowRuns AS SELECT * FROM main.WorkflowRuns WHERE closure_read_barrier()=1";
                await command.ExecuteNonQueryAsync(cancellationToken);
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }

    private const string ProjectId = "project-1";
    private const string PackageId = "pkg-1";
    private const string VersionId = "ver-1";
    private const string RunId = "run-1";

    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Timestamp = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Save_And_GetById_RoundTripsEveryAggregateDetail()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = CreateRunWithEvidence();

        await repository.SaveAsync(run);

        var fetched = await repository.GetByIdAsync(RunId);

        Assert.NotNull(fetched);
        Assert.Equal(RunId, fetched!.Id);
        Assert.Equal(ProjectId, fetched.ProjectId);
        Assert.Equal(PackageId, fetched.WorkflowPackageId);
        Assert.Equal(VersionId, fetched.WorkflowVersionId);
        Assert.Equal("session-1", fetched.SessionId);
        Assert.Equal(WorkflowRunState.Running, fetched.State);
        Assert.Equal("stage-2", fetched.CurrentStageId);
        Assert.Equal("Implementer", fetched.CurrentRole);
        Assert.Equal(Timestamp, fetched.StartedAtUtc);
        Assert.Null(fetched.EndedAtUtc);
        Assert.Equal(WorkflowTerminalOutcome.None, fetched.TerminalOutcome);
        Assert.Null(fetched.TerminalReason);

        var transition = Assert.Single(fetched.Transitions);
        Assert.Equal("stage-1", transition.FromStageId);
        Assert.Equal("stage-2", transition.ToStageId);
        Assert.Equal("Review passed.", transition.Reason);
        Assert.Equal(Timestamp.AddMinutes(10), transition.TriggeredAtUtc);

        var verdict = Assert.Single(transition.ReviewerVerdicts);
        Assert.Equal("Reviewer", verdict.ReviewerRole);
        Assert.Equal("route-review", verdict.RouteId);
        Assert.Equal(HashA, verdict.DocumentHash);
        Assert.Equal(WorkflowReviewVerdict.Approve, verdict.Verdict);
        Assert.Equal("Approved by the reviewer.", verdict.EvidenceSummary);

        Assert.NotNull(transition.UserApproval);
        Assert.Equal(UserApprovalDecision.Approved, transition.UserApproval!.Decision);

        // The authorizing artifact is mirrored in the payload as audit only; the run that comes back carries
        // no artifact at all, because SaveAsync wrote the run row and no artifact row.
        Assert.True(transition.HasAuthorizingArtifact);
        Assert.Equal("artifact-1", transition.AuthorizingArtifactId);
        Assert.Equal(HashA, transition.AuthorizingArtifactHash);
        Assert.Empty(fetched.Artifacts);

        var storedVerdict = Assert.Single(fetched.Verdicts);
        Assert.Equal(HashA, storedVerdict.DocumentHash);
        var storedApproval = Assert.Single(fetched.Approvals);
        Assert.Equal("approval-1", storedApproval.ApprovalId);
    }

    [Fact]
    public async Task SaveArtifactAsync_CommitsTheRowAndReadsItBackAsTheRunsEvidence()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = WorkflowRun.Start(
            RunId,
            ProjectId,
            PackageId,
            VersionId,
            "session-1",
            CreateStage("stage-1", artifactRequirement: "DocumentBundle"),
            Timestamp);

        var evidence = new WorkflowArtifactEvidence(
            "artifact-1",
            RunId,
            "stage-1",
            "DocumentBundle",
            HashA,
            HashA,
            Timestamp.AddMinutes(4),
            12,
            DataClassification.PrivateSource);

        // The artifact row references the run, so the run has to be there first - which is exactly the order
        // the run service records in: the run exists, and only then can an artifact belong to it.
        await repository.SaveAsync(run);
        await repository.SaveArtifactAsync(run.WithArtifact(evidence), evidence);

        var fetched = await repository.GetByIdAsync(RunId);

        Assert.NotNull(fetched);
        var loaded = Assert.Single(fetched!.Artifacts);
        Assert.Equal("artifact-1", loaded.ArtifactId);
        Assert.Equal(RunId, loaded.RunId);
        Assert.Equal("stage-1", loaded.StageId);
        Assert.Equal("DocumentBundle", loaded.Kind);
        Assert.Equal(HashA, loaded.BlobId);
        Assert.Equal(HashA, loaded.HashSha256);
        Assert.Equal(Timestamp.AddMinutes(4), loaded.CreatedAtUtc);
        Assert.Equal(12, loaded.SizeBytes);
        Assert.Equal(DataClassification.PrivateSource, loaded.Classification);

        // Every read of the run answers with the same evidence.
        Assert.Single((await repository.GetByProjectIdAsync(ProjectId)).Single().Artifacts);
        Assert.Empty((await repository.GetByProjectIdAsync("project-missing")).Select(run => run.Artifacts));
    }

    [Fact]
    public async Task AFailedArtifactInsertRollsBackTheRunRowWithIt()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = WorkflowRun.Start(
            RunId,
            ProjectId,
            PackageId,
            VersionId,
            "session-1",
            CreateStage("stage-1", artifactRequirement: "DocumentBundle", nextStageId: "stage-2"),
            Timestamp);

        var evidence = new WorkflowArtifactEvidence(
            "artifact-1",
            RunId,
            "stage-1",
            "DocumentBundle",
            HashA,
            HashA,
            Timestamp.AddMinutes(4),
            12,
            DataClassification.PrivateSource);

        await repository.SaveAsync(run);
        await repository.SaveArtifactAsync(run.WithArtifact(evidence), evidence);

        var committed = await repository.GetByIdAsync(RunId);

        // A run that moved to another stage, so a run row carrying it would be visibly different from the
        // committed one, and a second artifact that re-uses an artifact id that is already committed. The
        // insert has to fail, and the run row it was written with has to go with it.
        var moved = new WorkflowRun(
            RunId,
            ProjectId,
            PackageId,
            VersionId,
            "session-1",
            WorkflowRunState.Running,
            "stage-2",
            "Implementer",
            Timestamp,
            null,
            WorkflowTerminalOutcome.None,
            null,
            run.Transitions,
            run.Verdicts,
            run.Approvals);

        var duplicate = new WorkflowArtifactEvidence(
            "artifact-1",
            RunId,
            "stage-2",
            "DocumentBundle",
            HashA,
            HashA,
            Timestamp.AddMinutes(30),
            12,
            DataClassification.PrivateSource);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.SaveArtifactAsync(moved.WithArtifact(duplicate), duplicate));

        var reread = await repository.GetByIdAsync(RunId);
        Assert.Equal("stage-1", reread!.CurrentStageId);
        Assert.Equal("Coordinator", reread.CurrentRole);
        Assert.Equal(evidence.ArtifactId, Assert.Single(reread.Artifacts).ArtifactId);
        Assert.Equal(evidence.CreatedAtUtc, Assert.Single(reread.Artifacts).CreatedAtUtc);
        Assert.Equal(
            1,
            await _database.CountAsync("Artifacts"));
        Assert.Equal(committed!.StartedAtUtc, reread.StartedAtUtc);
    }

    [Fact]
    public async Task AnArtifactOfAnotherRunIsRefusedByTheRepository()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = WorkflowRun.Start(
            RunId,
            ProjectId,
            PackageId,
            VersionId,
            "session-1",
            CreateStage("stage-1"),
            Timestamp);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveArtifactAsync(
            run,
            new WorkflowArtifactEvidence(
                "artifact-1",
                "run-other",
                "stage-1",
                "DocumentBundle",
                HashA,
                HashA,
                Timestamp.AddMinutes(4),
                12,
                DataClassification.PrivateSource)));
    }

    [Fact]
    public async Task Save_UpdatesMutableStateButKeepsThePinnedVersionAndStartTime()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var run = CreateRunWithEvidence();

        await repository.SaveAsync(run);

        var replacement = new WorkflowRun(
            RunId,
            ProjectId,
            PackageId,
            "ver-moved",
            "session-1",
            WorkflowRunState.Completed,
            "stage-final",
            "Coordinator",
            Timestamp.AddDays(1),
            Timestamp.AddDays(1).AddHours(2),
            WorkflowTerminalOutcome.Completed,
            "Finished.",
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>());

        await repository.SaveAsync(replacement);

        var fetched = await repository.GetByIdAsync(RunId);

        Assert.NotNull(fetched);
        Assert.Equal(VersionId, fetched!.WorkflowVersionId);
        Assert.Equal(PackageId, fetched.WorkflowPackageId);
        Assert.Equal(ProjectId, fetched.ProjectId);
        Assert.Equal(Timestamp, fetched.StartedAtUtc);
        Assert.Equal(WorkflowRunState.Completed, fetched.State);
        Assert.Equal(WorkflowTerminalOutcome.Completed, fetched.TerminalOutcome);
        Assert.Equal("stage-final", fetched.CurrentStageId);
        Assert.Equal("Finished.", fetched.TerminalReason);
        Assert.Empty(fetched.Transitions);
        Assert.Empty(fetched.Verdicts);
        Assert.Empty(fetched.Approvals);
    }

    [Fact]
    public async Task GetActiveByProjectId_ReturnsTheMostRecentNonTerminalRun()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        var terminal = CreateRun("run-terminal", WorkflowRunState.Completed, Timestamp.AddHours(3));
        var active = CreateRun("run-active", WorkflowRunState.Running, Timestamp.AddHours(1));
        var newestActive = CreateRun("run-newest-active", WorkflowRunState.Suspended, Timestamp.AddHours(2));

        await repository.SaveAsync(terminal);
        await repository.SaveAsync(active);
        await repository.SaveAsync(newestActive);

        var fetched = await repository.GetActiveByProjectIdAsync(ProjectId);

        Assert.NotNull(fetched);
        Assert.Equal("run-newest-active", fetched!.Id);

        await ExecuteAsync("UPDATE WorkflowRuns SET State = 'Cancelled', EndedAtUtc = $ended, TerminalOutcome = 'Cancelled' WHERE Id = 'run-newest-active';",
            ("$ended", TestDatabase.FormatTimestamp(Timestamp.AddHours(4))));
        await ExecuteAsync("UPDATE WorkflowRuns SET State = 'Failed', EndedAtUtc = $ended, TerminalOutcome = 'Failed' WHERE Id = 'run-active';",
            ("$ended", TestDatabase.FormatTimestamp(Timestamp.AddHours(4))));

        Assert.Null(await repository.GetActiveByProjectIdAsync(ProjectId));
        Assert.Null(await repository.GetActiveByProjectIdAsync("project-missing"));
    }

    [Fact]
    public async Task GetByProjectId_ReturnsEveryRunOfTheProjectInStartOrder()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        await repository.SaveAsync(CreateRun("run-later", WorkflowRunState.Running, Timestamp.AddHours(2)));
        await repository.SaveAsync(CreateRun("run-earlier", WorkflowRunState.Running, Timestamp.AddHours(1)));

        var runs = await repository.GetByProjectIdAsync(ProjectId);

        Assert.Equal(
            new[] { "run-earlier", "run-later" },
            runs.Select(run => run.Id).ToArray());
        Assert.Empty(await repository.GetByProjectIdAsync("project-missing"));
    }

    [Fact]
    public async Task EvidenceRedactedJson_PersistsTheDetailedDomainEvidence()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        await repository.SaveAsync(CreateRunWithEvidence());

        var json = await ReadScalarAsync("SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id = 'run-1';");

        Assert.NotNull(json);

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.Equal("stage-2", root.GetProperty("currentStageId").GetString());
        Assert.Equal("Implementer", root.GetProperty("currentRole").GetString());

        var transition = root.GetProperty("transitions")[0];
        Assert.Equal("stage-1", transition.GetProperty("fromStageId").GetString());
        Assert.Equal("stage-2", transition.GetProperty("toStageId").GetString());
        Assert.Equal("Approve", transition.GetProperty("reviewerVerdicts")[0].GetProperty("verdict").GetString());
        Assert.Equal("Approved", transition.GetProperty("userApproval").GetProperty("decision").GetString());

        var verdict = root.GetProperty("verdicts")[0];
        Assert.Equal("Reviewer", verdict.GetProperty("reviewerRole").GetString());
        Assert.Equal(HashA, verdict.GetProperty("documentHash").GetString());

        var approval = root.GetProperty("approvals")[0];
        Assert.Equal("approval-1", approval.GetProperty("approvalId").GetString());
        Assert.Equal("user-1", approval.GetProperty("approvedBy").GetString());
    }

    [Fact]
    public async Task GetById_ReturnsNullForAMissingRun()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        Assert.Null(await repository.GetByIdAsync("run-missing"));
    }

    [Fact]
    public async Task GetById_RejectsARowWithoutAnEvidencePayload()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        await ExecuteAsync(
            """
            INSERT INTO WorkflowRuns
                (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, SessionId, State, StartedAtUtc,
                 EndedAtUtc, TerminalOutcome, EvidenceRedactedJson)
            VALUES ('run-raw', $projectId, $packageId, $versionId, NULL, 'Running', $startedAt,
                    NULL, 'None', NULL);
            """,
            ("$projectId", ProjectId),
            ("$packageId", PackageId),
            ("$versionId", VersionId),
            ("$startedAt", TestDatabase.FormatTimestamp(Timestamp)));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetByIdAsync("run-raw"));
    }

    [Fact]
    public async Task Save_CascadesWhenTheProjectIsDeleted()
    {
        await InitializeRunDependenciesAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        await repository.SaveAsync(CreateRunWithEvidence());

        await ExecuteAsync("DELETE FROM Projects WHERE Id = 'project-1';");

        Assert.Equal(0, await _database.CountAsync("WorkflowRuns"));
    }

    private async Task InitializeRunDependenciesAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            PackageId,
            "Run package",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            Timestamp,
            Timestamp));

        await versionRepository.UpsertAsync(new WorkflowVersion(
            VersionId,
            PackageId,
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Timestamp,
            null));

        await _database.SeedSessionAsync(
            sessionId: "session-1",
            projectId: ProjectId);
    }

    private static WorkflowRun CreateRunWithEvidence()
    {
        var initialStage = new WorkflowStageDefinition(
            "stage-1",
            "Stage 1",
            "Coordinator",
            WorkflowStageKind.Custom,
            new[] { "Reviewer" },
            requiresUserApproval: true,
            artifactRequirement: "DocumentBundle",
            nextStageId: "stage-2",
            failureStageId: null);

        var nextStage = new WorkflowStageDefinition(
            "stage-2",
            "Stage 2",
            "Implementer",
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId: null,
            failureStageId: null);

        var run = WorkflowRun.Start(
            RunId,
            ProjectId,
            PackageId,
            VersionId,
            "session-1",
            initialStage,
            Timestamp);

        // The stage is gated by the artifact it recorded, so the document bundle is recorded first and the
        // verdicts and the approval below pin its hash.
        run = run.WithArtifact(new WorkflowArtifactEvidence(
            "artifact-1",
            RunId,
            "stage-1",
            "DocumentBundle",
            HashA,
            HashA,
            Timestamp.AddMinutes(4),
            12,
            DataClassification.PrivateSource));

        run.RecordReviewerVerdict(new ReviewerVerdictRecord(
            "Reviewer",
            "route-review",
            HashA,
            WorkflowReviewVerdict.Approve,
            "Approved by the reviewer.",
            Timestamp.AddMinutes(5),
            "execution-review",
            "stage-1",
            "artifact-1"));

        run.RecordUserApproval(new UserApprovalEvidence(
            "approval-1",
            "user-1",
            "stage-1",
            HashA,
            UserApprovalDecision.Approved,
            "Approved by the user.",
            Timestamp.AddMinutes(6)));

        run.AdvanceTo(initialStage, nextStage, "Review passed.", Timestamp.AddMinutes(10));

        return run;
    }

    private static WorkflowStageDefinition CreateStage(
        string stageId,
        string? artifactRequirement = null,
        string? nextStageId = null)
    {
        return new WorkflowStageDefinition(
            stageId,
            $"Stage {stageId}",
            "Coordinator",
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement,
            nextStageId,
            failureStageId: null);
    }

    private static WorkflowRun CreateRun(string runId, WorkflowRunState state, DateTimeOffset startedAt)    {
        var terminal = WorkflowRun.IsTerminalState(state);

        return new WorkflowRun(
            runId,
            ProjectId,
            PackageId,
            VersionId,
            sessionId: null,
            state,
            "stage-1",
            "Coordinator",
            startedAt,
            terminal ? startedAt.AddMinutes(30) : null,
            terminal
                ? state == WorkflowRunState.Failed
                    ? WorkflowTerminalOutcome.Failed
                    : state == WorkflowRunState.Cancelled
                        ? WorkflowTerminalOutcome.Cancelled
                        : WorkflowTerminalOutcome.Completed
                : WorkflowTerminalOutcome.None,
            terminal ? "Finished." : null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>());
    }

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
