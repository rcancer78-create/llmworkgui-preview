using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Tests.TestSupport;

/// <summary>
/// The run store a test drives without the durable storage underneath it. Verdicts, approvals and artifacts
/// are kept exactly as the aggregate holds them, and the stored evidence payload is honoured on read, so a
/// test can load a run as it was persisted rather than as the object it last mutated.
/// </summary>
internal sealed class InMemoryWorkflowRunRepository : IWorkflowRunRepository
{
    private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

    public IReadOnlyCollection<WorkflowRun> Runs => _runs.Values.ToArray();

    public int SaveCount { get; private set; }

    public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        _runs[run.Id] = run;

        return Task.CompletedTask;
    }

    public Task SaveArtifactAsync(
        WorkflowRun run,
        WorkflowArtifactEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        _runs[run.Id] = run;

        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_runs.GetValueOrDefault(id));

    public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowRun>>(
            _runs.Values.Where(run => run.ProjectId == projectId).ToArray());

    public Task<WorkflowRun?> GetActiveByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _runs.Values.FirstOrDefault(run => run.ProjectId == projectId && !run.IsTerminal));

    /// <summary>
    /// Replaces the run's stored scheme snapshot with an unreadable document, the way a corrupted row would
    /// look. The service must refuse to fall back on the process-wide scheme.
    /// </summary>
    public void CorruptSchemeSnapshot(string runId)
    {
        var pinned = _runs[runId];

        _runs[runId] = new WorkflowRun(
            pinned.Id,
            pinned.ProjectId,
            pinned.WorkflowPackageId,
            pinned.WorkflowVersionId,
            pinned.SessionId,
            pinned.State,
            pinned.CurrentStageId,
            pinned.CurrentRole,
            pinned.StartedAtUtc,
            pinned.EndedAtUtc,
            pinned.TerminalOutcome,
            pinned.TerminalReason,
            pinned.Transitions,
            pinned.Verdicts,
            pinned.Approvals,
            pinned.TemplateId,
            pinned.TemplateVersion,
            pinned.TemplateGraphSnapshotJson,
            "not json",
            pinned.Artifacts);
    }
}

/// <summary>
/// The route store a test drives. It holds only the routes a test explicitly puts in it, so an id that was
/// never assigned is genuinely not a row - the same answer the real store gives for
/// <c>route-opencode</c>.
/// </summary>
internal sealed class InMemoryRouteRepository : IRouteRepository
{
    private readonly Dictionary<string, WorkflowRouteAssignment> _assignments = new(StringComparer.Ordinal);

    /// <summary>
    /// Every id this store was ever asked to create a row for. It is always empty: the reader has no write
    /// path at all, which is what makes "a refused request never invents a route" a structural fact rather
    /// than a convention.
    /// </summary>
    public IReadOnlyCollection<string> Written => Array.Empty<string>();

    /// <summary>Assigns the default complete assignment only when the test has not described the route itself.</summary>
    public void AssignIfAbsent(string routeId) => Assign(routeId, overwrite: false);

    public void Assign(
        string routeId,
        IReadOnlyList<WorkflowRouteIdentity>? missing = null,
        bool enabled = true,
        bool overwrite = true)
    {
        if (!overwrite && _assignments.ContainsKey(routeId))
        {
            return;
        }

        _assignments[routeId] = new WorkflowRouteAssignment(
            new Route(
                routeId,
                new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", null, null, null),
                DataClassification.PrivateSource,
                enabled,
                HealthState.Healthy,
                0),
            missing ?? Array.Empty<WorkflowRouteIdentity>());
    }

    public Task<WorkflowRouteAssignment?> GetAssignmentAsync(
        string routeId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_assignments.GetValueOrDefault(routeId));
}

internal sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);

    public List<Project> Projects { get; } = new();

    public void Add(Project project)
    {
        _projects[project.Id] = project;
        Projects.Add(project);
    }

    public Task UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        Add(project);

        return Task.CompletedTask;
    }

    public Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.GetValueOrDefault(projectId));

    public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.Values.FirstOrDefault(project => project.RootPath == rootPath));

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Project>>(_projects.Values.ToArray());

    public Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_projects.Remove(projectId));
}

internal sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Session> Sessions => _sessions.Values.ToArray();

    public Task UpsertAsync(Session session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;

        return Task.CompletedTask;
    }

    public Task<Session?> GetByIdAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.GetValueOrDefault(sessionId));

    public Task<IReadOnlyList<Session>> ListByProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            _sessions.Values.Where(session => session.ProjectId == projectId).ToArray());

    public Task<IReadOnlyList<Session>> ListByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            _sessions.Values.Where(session => session.Binding.AccountId == accountId).ToArray());

    public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.Remove(sessionId));
}

internal sealed class InMemoryExecutionRepository : IExecutionRepository
{
    private readonly Dictionary<string, Execution> _executions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Execution> Executions => _executions.Values.ToArray();

    /// <summary>How many times an execution was written, so "never retried" is observable.</summary>
    public int UpsertCount { get; private set; }

    public Task UpsertAsync(Execution execution, CancellationToken cancellationToken = default)
    {
        UpsertCount++;
        _executions[execution.Id] = execution;

        return Task.CompletedTask;
    }

    public Task<Execution?> GetByIdAsync(string executionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_executions.GetValueOrDefault(executionId));

    public Task<IReadOnlyList<Execution>> ListBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Execution>>(
            _executions.Values.Where(execution => execution.SessionId == sessionId).ToArray());

    public Task AppendEventAsync(
        ExecutionEventRecord executionEvent,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ExecutionEventRecord>> ListEventsAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExecutionEventRecord>>(Array.Empty<ExecutionEventRecord>());
}

/// <summary>
/// The reviewer-execution evidence store a test drives, with the same rules the durable store enforces: a
/// binding is written before its turn with no observed route, only the observed route and the execution state
/// may change afterwards, and an observed route is only ever what the execution row already recorded.
/// <para>
/// The last rule is the one that needs a collaborator. Reading the observed route out of the execution -
/// rather than out of the binding the same process just wrote - is what stops a caller from promoting a
/// requested route into an observed one, so a double that simply stored whatever it was handed would let a
/// test pass a gate the product refuses. A test that supplies no execution store therefore keeps the
/// original two-column behaviour and must not be read as evidence about that rule.
/// </para>
/// </summary>
internal sealed class InMemoryReviewEvidenceRepository : IWorkflowReviewEvidenceRepository
{
    private readonly Dictionary<string, ReviewerExecutionEvidence> _byExecution = new(StringComparer.Ordinal);
    private readonly InMemoryExecutionRepository? _executions;

    public InMemoryReviewEvidenceRepository(InMemoryExecutionRepository? executions = null) =>
        _executions = executions;

    public IReadOnlyCollection<ReviewerExecutionEvidence> Saved => _byExecution.Values.ToArray();

    public Task<IReadOnlyList<ReviewerExecutionEvidence>> ListByRunIdAsync(
        string workflowRunId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReviewerExecutionEvidence>>(
            _byExecution.Values
                .Where(evidence => evidence.WorkflowRunId == workflowRunId)
                .OrderBy(evidence => evidence.ExecutionId, StringComparer.Ordinal)
                .ToArray());

    public Task<ReviewerExecutionEvidence?> GetByExecutionIdAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_byExecution.GetValueOrDefault(executionId));

    public Task SaveAsync(ReviewerExecutionEvidence evidence, CancellationToken cancellationToken = default)
    {
        if (_byExecution.ContainsKey(evidence.ExecutionId))
        {
            throw new InvalidOperationException($"Reviewer execution '{evidence.ExecutionId}' is already bound.");
        }

        if (evidence.ObservedRouteId is not null)
        {
            throw new ArgumentException(
                "A reviewer execution binding is written before the turn is dispatched.",
                nameof(evidence));
        }

        var duplicate = _byExecution.Values.FirstOrDefault(existing =>
            existing.WorkflowRunId == evidence.WorkflowRunId
            && existing.StageId == evidence.StageId
            && existing.ReviewerRole == evidence.ReviewerRole
            && existing.ReviewedArtifactHash == evidence.ReviewedArtifactHash
            && existing.IsReadOnly == evidence.IsReadOnly
            && existing.ExecutionState is not (ExecutionState.Failed or ExecutionState.Cancelled));

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"A reviewer execution for run '{evidence.WorkflowRunId}', stage '{evidence.StageId}', role "
                    + $"'{evidence.ReviewerRole}' and hash '{evidence.ReviewedArtifactHash}' is already bound "
                    + $"to execution '{duplicate.ExecutionId}'.");
        }

        _byExecution[evidence.ExecutionId] = evidence;

        return Task.CompletedTask;
    }

    public async Task UpdateObservedAsync(
        ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        if (!_byExecution.TryGetValue(evidence.ExecutionId, out var stored))
        {
            throw new InvalidOperationException(
                $"Reviewer execution '{evidence.ExecutionId}' has no binding to update.");
        }

        if (_executions is not null
            && !string.Equals(
                (await _executions.GetByIdAsync(evidence.ExecutionId, cancellationToken).ConfigureAwait(false))
                    ?.ObservedRouteId,
                evidence.ObservedRouteId,
                StringComparison.Ordinal))
        {
            // The same no-op the durable store performs through the WHERE clause of its own UPDATE and the
            // database guard beside it: an observation the execution did not record is not written, so the
            // binding keeps authorizing nothing rather than being corrected into a claim.
            return;
        }

        // The durable UPDATE changes observations only; caller-supplied identity never replaces
        // the immutable run/stage/role/artifact binding.
        _byExecution[evidence.ExecutionId] = stored.WithObservedOutcome(
            evidence.ObservedRouteId, evidence.ExecutionState);
    }
}
