using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Hosting;

public sealed class StartupWorkflowRecoveryTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("Suspended")]
    public async Task InitializeAsync_TerminalizesInterruptedWorkflowAndPreservesCompletedRun(string state)
    {
        await SeedAsync(state);
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _database.Root);

        await HostBootstrapper.InitializeAsync(host, recoverInterruptedWorkflows: true);

        var repository = host.Services.GetRequiredService<IWorkflowRunRepository>();
        var interrupted = await repository.GetByIdAsync("run-interrupted");
        var completed = await repository.GetByIdAsync("run-completed");
        Assert.NotNull(interrupted);
        Assert.Equal(WorkflowRunState.Failed, interrupted!.State);
        Assert.Equal(WorkflowTerminalOutcome.Failed, interrupted.TerminalOutcome);
        Assert.Equal(AppCrashRecoveryService.WorkflowRunFailureReason, interrupted.TerminalReason);
        Assert.NotNull(interrupted.EndedAtUtc);
        Assert.Equal(WorkflowRunState.Completed, completed!.State);
        Assert.Equal(WorkflowTerminalOutcome.Completed, completed.TerminalOutcome);
        Assert.Equal("version-1", interrupted.WorkflowVersionId);
        Assert.Empty(interrupted.Verdicts);
        Assert.Empty(interrupted.Approvals);

        // Repeated startup must not rewrite terminal evidence or invent a retry run.
        var endedAt = interrupted.EndedAtUtc;
        await HostBootstrapper.InitializeAsync(host, recoverInterruptedWorkflows: true);
        Assert.Equal(endedAt, (await repository.GetByIdAsync("run-interrupted"))!.EndedAtUtc);
        Assert.Equal(2, await _database.CountAsync("WorkflowRuns"));
    }

    [Fact]
    public async Task InitializeAsync_ViewOnlyDoesNotRecoverWorkflowRuns()
    {
        await SeedAsync("Running");
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _database.Root)
            .ConfigureServices(services => services.AddSingleton<IApplicationInstanceGuard>(new ViewOnlyGuard()))
            .Build();

        await HostBootstrapper.InitializeAsync(host, recoverInterruptedWorkflows: true);

        var run = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync("run-interrupted");
        Assert.Equal(WorkflowRunState.Running, run!.State);
        Assert.Null(run.EndedAtUtc);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
    }

    [Fact]
    public async Task InitializeAsync_CleanLifetimePreservesSavedWorkflowForManualContinuation()
    {
        await SeedAsync("Running");
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _database.Root);
        await HostBootstrapper.InitializeAsync(host);
        var run = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync("run-interrupted");
        Assert.Equal(WorkflowRunState.Running, run!.State);
        Assert.Null(run.EndedAtUtc);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
    }

    [Theory]
    [InlineData(true, true, "native-test", "session-1", true)]
    [InlineData(false, true, "native-test", "session-1", false)]
    [InlineData(true, false, "native-test", "session-1", false)]
    [InlineData(true, true, null, "session-1", false)]
    [InlineData(true, true, "native-test", "other-session", false)]
    public async Task InitializeAsync_PreservesLinkedRunOnlyWithFreshMatchingReattachment(
        bool processAlive, bool bindingMatched, string? nativeSessionId, string evidenceSessionId, bool preservesRun)
    {
        await SeedAsync("Running");
        await _database.SeedSessionAsync(state: "Active", nativeSessionId: "native-test");
        await _database.SeedExecutionAsync("execution-1", state: "Ambiguous");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE WorkflowRuns SET SessionId = 'session-1' WHERE Id = 'run-interrupted';";
            await command.ExecuteNonQueryAsync();
        }
        var evidence = new ReconciliationEvidence
        {
            LocalSessionId = evidenceSessionId, ExecutionId = "execution-1", NativeSessionId = nativeSessionId,
            Outcome = LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome.Reattached,
            ProcessAlive = processAlive, BindingMatched = bindingMatched, Details = "Synthetic reconciliation result",
            ReconciledAtUtc = DateTimeOffset.UtcNow
        };
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _database.Root)
            .ConfigureServices(services => services.AddSingleton<IReconciliationService>(new FixedReconciliation(evidence)))
            .Build();

        await HostBootstrapper.InitializeAsync(host, recoverInterruptedWorkflows: true);

        var run = await host.Services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync("run-interrupted");
        Assert.Equal(preservesRun ? WorkflowRunState.Running : WorkflowRunState.Failed, run!.State);
        Assert.Equal(SessionState.Active, (await host.Services.GetRequiredService<ISessionRepository>().GetByIdAsync("session-1"))!.State);
        Assert.Equal(ExecutionState.Ambiguous, (await host.Services.GetRequiredService<IExecutionRepository>().GetByIdAsync("execution-1"))!.State);
        Assert.NotNull(await host.Services.GetRequiredService<IProjectLockRepository>().GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task InitializeAsync_RecoversEveryInterruptedRunAndIgnoresPersistedReattachmentFlag()
    {
        await SeedAsync("Running");
        await _database.SeedSessionAsync(state: "Active", reconciliationOutcome: "Reattached", nativeSessionId: "native-old");
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE WorkflowRuns SET SessionId = 'session-1' WHERE Id = 'run-interrupted';
                INSERT INTO WorkflowRuns
                    (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, TerminalOutcome, EvidenceRedactedJson)
                SELECT 'run-second', ProjectId, WorkflowPackageId, WorkflowVersionId, 'Running', StartedAtUtc, 'None', EvidenceRedactedJson
                FROM WorkflowRuns WHERE Id = 'run-interrupted';
                """;
            await command.ExecuteNonQueryAsync();
        }
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _database.Root)
            .ConfigureServices(services => services.AddSingleton<IReconciliationService>(new FixedReconciliation()))
            .Build();
        await HostBootstrapper.InitializeAsync(host, recoverInterruptedWorkflows: true);
        var repository = host.Services.GetRequiredService<IWorkflowRunRepository>();
        Assert.Equal(WorkflowRunState.Failed, (await repository.GetByIdAsync("run-interrupted"))!.State);
        Assert.Equal(WorkflowRunState.Failed, (await repository.GetByIdAsync("run-second"))!.State);
        Assert.Equal(WorkflowRunState.Completed, (await repository.GetByIdAsync("run-completed"))!.State);
    }

    private async Task SeedAsync(string state)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('package-1', 'Synthetic recovery package', 'Imported', $hash, $hash, $at, $at);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ('version-1', 'package-1', 1, $hash, $hash, 'Imported', $at);
            INSERT INTO WorkflowRuns
                (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EndedAtUtc,
                 TerminalOutcome, EvidenceRedactedJson)
            VALUES ('run-interrupted', 'project-1', 'package-1', 'version-1', $state, $at, NULL, 'None', $evidence),
                   ('run-completed', 'project-1', 'package-1', 'version-1', 'Completed', $at, $at, 'Completed', $evidence);
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$hash", "sha256:" + new string('a', 64));
        command.Parameters.AddWithValue("$at", TestDatabase.FormatTimestamp(DateTimeOffset.UnixEpoch));
        command.Parameters.AddWithValue("$evidence", """{"currentStageId":"implementation","currentRole":"Coder"}""");
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-recovery-test";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException("View only");
        public void Dispose() { }
    }

    private sealed class FixedReconciliation(params ReconciliationEvidence[] evidence) : IReconciliationService
    {
        public Task<IReadOnlyList<ReconciliationEvidence>> ReconcileAllActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReconciliationEvidence>>(evidence);
        public Task<ReconciliationEvidence> ReconcileSessionAsync(string localSessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ReconciliationRecoveryResult> ApplyRecoveryActionAsync(string localSessionId, RecoveryAction action, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
