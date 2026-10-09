using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class RepositoryCRUDTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ProjectRepository_UpsertsReadsListsAndDeletes()
    {
        await _database.InitializeAsync();

        var repository = new SqliteProjectRepository(_database.Factory);
        var rootPath = _database.GetWorkspacePath("project-a");

        var project = new Project(
            "project-a",
            "Project A",
            rootPath,
            "main",
            isDirty: false,
            hasRequiredInstructions: true,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource);

        await repository.UpsertAsync(project);

        var loaded = await repository.GetByIdAsync("project-a");

        Assert.NotNull(loaded);
        Assert.Equal("Project A", loaded!.DisplayName);
        Assert.Equal(ProjectLock.CanonicalizeRoot(rootPath), loaded.RootPath);
        Assert.Equal("main", loaded.GitBranch);
        Assert.True(loaded.HasRequiredInstructions);
        Assert.False(loaded.IsDirty);

        var byRootPath = await repository.GetByRootPathAsync(rootPath);

        Assert.NotNull(byRootPath);
        Assert.Equal("project-a", byRootPath!.Id);

        await repository.UpsertAsync(new Project(
            "project-a",
            "Project A Renamed",
            rootPath,
            gitBranch: null,
            isDirty: true,
            hasRequiredInstructions: false,
            defaultWorkflowId: "workflow-1",
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource));

        var reloaded = await repository.GetByIdAsync("project-a");

        Assert.NotNull(reloaded);
        Assert.Equal("Project A Renamed", reloaded!.DisplayName);
        Assert.True(reloaded.IsDirty);
        Assert.Equal("workflow-1", reloaded.DefaultWorkflowId);
        Assert.Single(await repository.ListAsync());

        Assert.True(await repository.DeleteAsync("project-a"));
        Assert.Null(await repository.GetByIdAsync("project-a"));
        Assert.False(await repository.DeleteAsync("project-a"));
    }

    [Fact]
    public async Task SessionRepository_UpsertsReadsAndListsByProject()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        var repository = new SqliteSessionRepository(_database.Factory);
        var createdAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var binding = new SessionBinding(
            BackendType.OpenCode,
            "provider-1",
            "account-1",
            "model-1",
            "high",
            "fast",
            "interactive");

        var session = new Session(
            "session-1",
            binding,
            "project-1",
            _database.GetWorkspacePath(),
            "native-1",
            SessionState.Active,
            ReconciliationOutcome.Reattached,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: null,
            role: "Executor",
            activeExecutionId: null,
            createdAt,
            createdAt.AddMinutes(5));

        await repository.UpsertAsync(session);

        var loaded = await repository.GetByIdAsync("session-1");

        Assert.NotNull(loaded);
        Assert.Equal(BackendType.OpenCode, loaded!.Binding.Backend);
        Assert.Equal("provider-1", loaded.Binding.ProviderProfileId);
        Assert.Equal("account-1", loaded.Binding.AccountId);
        Assert.Equal("model-1", loaded.Binding.ModelId);
        Assert.Equal("high", loaded.Binding.ReasoningEffort);
        Assert.Equal("fast", loaded.Binding.SpeedMode);
        Assert.Equal("interactive", loaded.Binding.ExecutionMode);
        Assert.Equal(SessionState.Active, loaded.State);
        Assert.Equal(ReconciliationOutcome.Reattached, loaded.ReconciliationOutcome);
        Assert.Equal(CloseReason.None, loaded.CloseReason);
        Assert.Equal("native-1", loaded.NativeSessionId);
        Assert.Equal("Executor", loaded.Role);
        Assert.Equal(createdAt, loaded.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(5), loaded.LastEventAt);

        var byProject = await repository.ListByProjectAsync("project-1");

        Assert.Single(byProject);
        Assert.Equal("session-1", byProject[0].Id);
        Assert.Empty(await repository.ListByProjectAsync("missing-project"));

        Assert.True(await repository.DeleteAsync("session-1"));
        Assert.Null(await repository.GetByIdAsync("session-1"));
    }

    [Fact]
    public async Task ExecutionRepository_UpsertsExecutionsWithRedactedEvents()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync();

        var repository = new SqliteExecutionRepository(_database.Factory, new SensitiveDataFilter());
        var createdAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var execution = new Execution(
            "execution-1",
            "session-1",
            "client-request-1",
            ExecutionState.Running,
            ExecutionFailureReason.None,
            "route-1",
            observedRouteId: null,
            retryOfExecutionId: null,
            processState: "running",
            exitCode: null,
            terminationReason: null,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: "hash-before",
            sourceHashAfter: null,
            createdAt,
            startedAt: createdAt,
            endedAt: null);

        await repository.UpsertAsync(execution);

        var loaded = await repository.GetByIdAsync("execution-1");

        Assert.NotNull(loaded);
        Assert.Equal(ExecutionState.Running, loaded!.State);
        Assert.Equal("route-1", loaded.RequestedRouteId);
        Assert.Equal("running", loaded.ProcessState);
        Assert.Equal("hash-before", loaded.SourceHashBefore);
        Assert.Empty(loaded.Artifacts);

        var bySession = await repository.ListBySessionAsync("session-1");

        Assert.Single(bySession);
        Assert.Equal("execution-1", bySession[0].Id);

        await repository.AppendEventAsync(new ExecutionEventRecord(
            "event-1",
            "execution-1",
            0,
            "Prompt",
            """{"note":"hello"}""",
            "plain output",
            DataClassification.PrivateSource,
            createdAt.AddSeconds(1)));

        await repository.AppendEventAsync(new ExecutionEventRecord(
            "event-2",
            "execution-1",
            1,
            "Output",
            NormalizedRedactedPayloadJson: null,
            RawRedactedPayloadText: "Authorization: Bearer abcdefghijklmnopqrstuvwxyz",
            DataClassification.PrivateSource,
            createdAt.AddSeconds(2)));

        var events = await repository.ListEventsAsync("execution-1");

        Assert.Equal(2, events.Count);
        Assert.Equal(0, events[0].Sequence);
        Assert.Equal("Prompt", events[0].EventKind);
        Assert.Equal("""{"note":"hello"}""", events[0].NormalizedRedactedPayloadJson);
        Assert.Equal("plain output", events[0].RawRedactedPayloadText);
        Assert.Equal(DataClassification.PrivateSource, events[0].DataClassification);
        Assert.Equal(1, events[1].Sequence);
        Assert.DoesNotContain(
            "abcdefghijklmnopqrstuvwxyz",
            events[1].RawRedactedPayloadText,
            StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", events[1].RawRedactedPayloadText!, StringComparison.Ordinal);

        await repository.UpsertAsync(new Execution(
            "execution-1",
            "session-1",
            "client-request-1",
            ExecutionState.Succeeded,
            ExecutionFailureReason.None,
            "route-1",
            observedRouteId: null,
            retryOfExecutionId: null,
            processState: null,
            exitCode: 0,
            terminationReason: null,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: "hash-before",
            sourceHashAfter: "hash-after",
            createdAt,
            startedAt: createdAt,
            endedAt: createdAt.AddMinutes(1)));

        var completed = await repository.GetByIdAsync("execution-1");

        Assert.NotNull(completed);
        Assert.Equal(ExecutionState.Succeeded, completed!.State);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal("hash-after", completed.SourceHashAfter);
        Assert.Equal(createdAt.AddMinutes(1), completed.EndedAt);
    }

    [Fact]
    public async Task ApplicationSettingsRepository_RoundTripsValues()
    {
        await _database.InitializeAsync();

        var repository = new SqliteApplicationSettingsRepository(_database.Factory);

        Assert.Null(await repository.GetValueAsync("theme"));

        await repository.SetValueAsync("theme", "dark");
        Assert.Equal("dark", await repository.GetValueAsync("theme"));

        await repository.SetValueAsync("theme", "light");
        await repository.SetValueAsync("zoom", "150", "Int32");

        var all = await repository.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal("light", all["theme"]);
        Assert.Equal("150", all["zoom"]);

        Assert.True(await repository.RemoveAsync("theme"));
        Assert.False(await repository.RemoveAsync("theme"));
        Assert.Null(await repository.GetValueAsync("theme"));
    }

    [Fact]
    public async Task HealthStateRepository_UpsertsByScope()
    {
        await _database.InitializeAsync();

        var repository = new SqliteHealthStateRepository(_database.Factory);
        var updatedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new HealthStateRecord(
            "health-1",
            "Account",
            "account-1",
            HealthState.Healthy,
            ErrorClass: null,
            FailureCount: 0,
            WindowStartedAt: null,
            CooldownUntil: null,
            EvidenceRedactedJson: null,
            updatedAt));

        var loaded = await repository.GetAsync("Account", "account-1");

        Assert.NotNull(loaded);
        Assert.Equal("health-1", loaded!.Id);
        Assert.Equal(HealthState.Healthy, loaded.State);
        Assert.Null(loaded.ErrorClass);

        await repository.UpsertAsync(new HealthStateRecord(
            "health-2",
            "Account",
            "account-1",
            HealthState.QuarantinedAuto,
            HealthErrorClass.NetworkOrTimeout,
            FailureCount: 3,
            WindowStartedAt: updatedAt,
            CooldownUntil: updatedAt.AddMinutes(30),
            EvidenceRedactedJson: """{"reason":"timeout"}""",
            updatedAt.AddMinutes(1)));

        var updated = await repository.GetAsync("Account", "account-1");

        Assert.NotNull(updated);
        Assert.Equal("health-1", updated!.Id);
        Assert.Equal(HealthState.QuarantinedAuto, updated.State);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, updated.ErrorClass);
        Assert.Equal(3, updated.FailureCount);
        Assert.Equal(updatedAt, updated.WindowStartedAt);
        Assert.Equal(updatedAt.AddMinutes(30), updated.CooldownUntil);
        Assert.Equal("""{"reason":"timeout"}""", updated.EvidenceRedactedJson);
        Assert.Single(await repository.ListAsync());
    }
}
